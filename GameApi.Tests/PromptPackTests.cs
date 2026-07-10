using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using GameApi.GameLoop;
using Xunit;

namespace GameApi.Tests;

// Phase 10: prompt packs + host selector. Covers the no-repeat picker and the
// pack-conditional AI line as unit tests, and the hub rules (host-only pack change,
// StartGame plays the selected pack) with real SignalR clients.
public class PromptPackTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public PromptPackTests(TestAppFactory factory) => _factory = factory;

    // ---- unit: no repeats within a game per pack ----

    [Theory]
    [InlineData("family")]
    [InlineData("deep")]
    [InlineData("office")]
    [InlineData("adult")]
    [InlineData("drinking")]
    [InlineData("trivia")]
    public void PickPrompt_NoRepeatsUntilPackExhausted(string key)
    {
        var pack = PromptPacks.Get(key);
        var used = new HashSet<int>();
        var picked = new List<string>();

        // Draw exactly one full pack's worth — every prompt must be distinct.
        for (var i = 0; i < pack.Prompts.Length; i++)
            picked.Add(PromptPacks.PickPrompt(key, used));

        Assert.Equal(pack.Prompts.Length, picked.Distinct().Count());
        Assert.All(picked, p => Assert.Contains(p, pack.Prompts));

        // One past the pack size resets the used set instead of looping forever.
        var afterExhaustion = PromptPacks.PickPrompt(key, used);
        Assert.Contains(afterExhaustion, pack.Prompts);
    }

    [Fact]
    public void Packs_HaveExpectedKeysInOrder()
    {
        // SFW packs first, 18+/21+ last (Kyle's explicit ordering).
        Assert.Equal(
            new[] { "family", "deep", "office", "trivia", "adult", "drinking" },
            PromptPacks.All.Select(p => p.Key));
        Assert.Equal(60, PromptPacks.Family.Prompts.Length);
        Assert.Equal(40, PromptPacks.Deep.Prompts.Length);
        Assert.Equal(40, PromptPacks.Office.Prompts.Length);
        Assert.Equal(40, PromptPacks.Adult.Prompts.Length);
        Assert.Equal(40, PromptPacks.Drinking.Prompts.Length);
        Assert.Equal(40, PromptPacks.Trivia.Prompts.Length);
        // The drinking pack description carries the standing responsible-drinking line.
        Assert.Contains("drink responsibly", PromptPacks.Drinking.Description);
    }

    [Fact]
    public void EveryPack_PromptsAreNonEmptyAndUnique()
    {
        foreach (var pack in PromptPacks.All)
        {
            Assert.NotEmpty(pack.Prompts);
            Assert.All(pack.Prompts, p => Assert.False(string.IsNullOrWhiteSpace(p)));
            Assert.Equal(pack.Prompts.Length, pack.Prompts.Distinct().Count());
        }
    }

    // deep/office carry no pack-specific AI nudge — they fall through PackGuidance's
    // default case, so the system prompt gets neither the trivia nor the crude line.
    [Theory]
    [InlineData("deep")]
    [InlineData("office")]
    public void SystemPrompt_NoExtraLine_ForDeepAndOffice(string key)
    {
        var prompt = GeminiBrain.BuildSystemPrompt(Ctx(key));
        Assert.DoesNotContain("THIS GAME IS TRIVIA", prompt);
        Assert.DoesNotContain("THIS GAME LEANS CRUDE", prompt);
        Assert.Contains("HOW TO NOT GET CAUGHT", prompt);
    }

    [Fact]
    public void UnknownPack_FallsBackToFamily_AndIsValidKeyRejectsIt()
    {
        Assert.False(PromptPacks.IsValidKey("nope"));
        Assert.False(PromptPacks.IsValidKey(null));
        Assert.Same(PromptPacks.Family, PromptPacks.Get("nope"));
    }

    // ---- unit: pack-conditional AI line (additive, AI-DESIGN rules intact) ----

    [Fact]
    public void SystemPrompt_TriviaLine_OnlyForTrivia()
    {
        var trivia = GeminiBrain.BuildSystemPrompt(Ctx("trivia"));
        Assert.Contains("THIS GAME IS TRIVIA", trivia);
        Assert.Contains("guessing from vague", trivia);

        var family = GeminiBrain.BuildSystemPrompt(Ctx("family"));
        Assert.DoesNotContain("THIS GAME IS TRIVIA", family);
        Assert.DoesNotContain("THIS GAME LEANS CRUDE", family);
        // The AI-DESIGN rules are still intact (additive only).
        Assert.Contains("HOW TO NOT GET CAUGHT", family);
    }

    [Theory]
    [InlineData("adult")]
    [InlineData("drinking")]
    public void SystemPrompt_CrassLine_ForAdultAndDrinking(string key)
    {
        var prompt = GeminiBrain.BuildSystemPrompt(Ctx(key));
        Assert.Contains("THIS GAME LEANS CRUDE", prompt);
        Assert.Contains("never escalate", prompt);
        Assert.DoesNotContain("THIS GAME IS TRIVIA", prompt);
    }

    private static AiTurnContext Ctx(string packKey) => new(
        CurrentPrompt: "worst purchase you ever made",
        RoundNumber: 1,
        AiDisplayName: "Riley",
        HumanDisplayNames: new[] { "Alex", "Sam" },
        History: System.Array.Empty<RoundHistory>(),
        PreviousOwnAnswers: System.Array.Empty<string>(),
        StyleSummaries: System.Array.Empty<string>(),
        GroupStats: new GroupStats(30, 0.8, 0.1, 0.05),
        TimingState: new AnswerTiming.State(),
        FallbackState: new FallbackState(),
        TimeRemaining: TimeSpan.FromSeconds(20),
        PackKey: packKey);

    // ---- hub: non-host SetLobbyOptions rejected ----

    [Fact]
    public async Task SetLobbyOptions_ByNonHost_IsRejected()
    {
        var hostToken = await RegisterAsync("host_" + Guid.NewGuid().ToString("N")[..8], "Hostie");
        var guestToken = await RegisterAsync("guest_" + Guid.NewGuid().ToString("N")[..8], "Guesty");

        await using var host = BuildConnection(hostToken);
        await using var guest = BuildConnection(guestToken);

        LobbyState? hostState = null;
        host.On<LobbyState>("LobbyUpdated", s => hostState = s);

        await host.StartAsync();
        await guest.StartAsync();

        await host.InvokeAsync("CreateLobby");
        await WaitFor(() => hostState != null, "host never got initial LobbyUpdated");
        await guest.InvokeAsync("JoinLobby", hostState!.Code);
        await WaitFor(() => hostState!.Players.Count == 2, "guest never joined");

        // Guest (not host) tries to change options → HubException.
        var ex = await Assert.ThrowsAsync<HubException>(() => guest.InvokeAsync("SetLobbyOptions", "trivia", "normal", "standard"));
        Assert.Contains("host", ex.Message);

        // Bad keys from the host are also rejected, each with its own message.
        var badEx = await Assert.ThrowsAsync<HubException>(() => host.InvokeAsync("SetLobbyOptions", "nope", "normal", "standard"));
        Assert.Contains("Unknown pack", badEx.Message);
        var badDiff = await Assert.ThrowsAsync<HubException>(() => host.InvokeAsync("SetLobbyOptions", "trivia", "impossible", "standard"));
        Assert.Contains("Unknown difficulty", badDiff.Message);
        var badPace = await Assert.ThrowsAsync<HubException>(() => host.InvokeAsync("SetLobbyOptions", "trivia", "normal", "warp"));
        Assert.Contains("Unknown pace", badPace.Message);
    }

    // ---- hub: StartGame plays the selected pack ----

    [Fact]
    public async Task SetLobbyOptions_ThenStartGame_UsesSelectedPack()
    {
        var names = new[] { "Hostie", "Guest1", "Guest2" };
        var conns = new List<HubConnection>();
        foreach (var n in names)
        {
            var token = await RegisterAsync(n.ToLower() + "_" + Guid.NewGuid().ToString("N")[..8], n);
            conns.Add(BuildConnection(token));
        }

        try
        {
            var host = conns[0];
            LobbyState? hostState = null;
            string? seenPack = null;
            string? firstPrompt = null;

            host.On<LobbyState>("LobbyUpdated", s => hostState = s);
            foreach (var c in conns)
            {
                c.On<string, string, string>("LobbyOptionsChanged", (p, _d, _pc) => seenPack = p);
                c.On<string, int, DateTime>("PromptStarted", (prompt, _, _2) => firstPrompt ??= prompt);
            }

            foreach (var c in conns) await c.StartAsync();

            await host.InvokeAsync("CreateLobby");
            await WaitFor(() => hostState != null, "no initial LobbyUpdated");
            var code = hostState!.Code;
            await conns[1].InvokeAsync("JoinLobby", code);
            await conns[2].InvokeAsync("JoinLobby", code);
            await WaitFor(() => hostState!.Players.Count == 3, "not all 3 joined");

            // Host selects TRIVIA — everyone should see LobbyOptionsChanged.
            await host.InvokeAsync("SetLobbyOptions", "trivia", "normal", "standard");
            await WaitFor(() => seenPack == "trivia", "LobbyOptionsChanged not broadcast");

            await host.InvokeAsync("StartGame");
            await WaitFor(() => firstPrompt != null, "no PromptStarted after start");

            // The first prompt must come from the trivia pack, not the default family one.
            Assert.Contains(firstPrompt, PromptPacks.Trivia.Prompts);
            Assert.DoesNotContain(firstPrompt, PromptPacks.Family.Prompts);
        }
        finally
        {
            foreach (var c in conns) await c.DisposeAsync();
        }
    }

    // ---- infra ----

    private async Task<string> RegisterAsync(string username, string displayName)
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new { username, displayName, password = "Password123" });
        if (!res.IsSuccessStatusCode)
            throw new Xunit.Sdk.XunitException($"register failed {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var body = await res.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.Token;
    }

    private HubConnection BuildConnection(string token) =>
        new HubConnectionBuilder()
            .WithUrl(_factory.Server.BaseAddress + "hubs/game", options =>
            {
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    private static async Task WaitFor(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(message);
    }

    private record AuthResponse(string Token, string DisplayName, string Username);
    private record LobbyState(string Code, string State, List<PlayerEntry> Players, string PackKey);
    private record PlayerEntry(string DisplayName, int TokensRemaining, bool IsConnected, bool IsHost);
}
