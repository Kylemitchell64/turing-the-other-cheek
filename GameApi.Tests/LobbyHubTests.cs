using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using GameApi.Lobbies;
using Xunit;

namespace GameApi.Tests;

// Phase 2 gate: two real authed SignalR clients create + join a lobby, both see
// LobbyUpdated, and StartGame with <3 players is rejected. Phase 3's playtest will
// extend this project (4 clients, full round loop).
public class LobbyHubTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public LobbyHubTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task TwoClients_CreateAndJoin_BothSeeRoster()
    {
        var hostToken = await RegisterAsync("host_" + Guid.NewGuid().ToString("N")[..8], "Hostie");
        var guestToken = await RegisterAsync("guest_" + Guid.NewGuid().ToString("N")[..8], "Guesty");

        await using var host = BuildConnection(hostToken);
        await using var guest = BuildConnection(guestToken);

        // Latest LobbyUpdated seen by each client.
        LobbyState? hostState = null;
        LobbyState? guestState = null;
        var hostGotTwo = new TaskCompletionSource();
        var guestGotOne = new TaskCompletionSource();

        host.On<LobbyState>("LobbyUpdated", s =>
        {
            hostState = s;
            if (s.Players.Count == 2) hostGotTwo.TrySetResult();
        });
        guest.On<LobbyState>("LobbyUpdated", s =>
        {
            guestState = s;
            guestGotOne.TrySetResult();
        });

        await host.StartAsync();
        await guest.StartAsync();

        await host.InvokeAsync("CreateLobby");

        // Wait for the host's own LobbyUpdated so we have a code to join with.
        await WaitFor(() => hostState != null, "host never got initial LobbyUpdated");
        var code = hostState!.Code;
        Assert.Equal(5, code.Length);

        await guest.InvokeAsync("JoinLobby", code);

        await Task.WhenAll(
            hostGotTwo.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            guestGotOne.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, hostState!.Players.Count);
        Assert.Equal(2, guestState!.Players.Count);
        Assert.Contains(hostState.Players, p => p.DisplayName == "Hostie" && p.IsHost);
        Assert.Contains(hostState.Players, p => p.DisplayName == "Guesty" && !p.IsHost);
    }

    [Fact]
    public async Task StartGame_WithTwoPlayers_IsRejected()
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
        await WaitFor(() => hostState!.Players.Count == 2, "guest never showed up");

        // 2 players < 3 required — must throw a HubException surfaced to the caller.
        var ex = await Assert.ThrowsAsync<HubException>(() => host.InvokeAsync("StartGame"));
        Assert.Contains("at least 3", ex.Message);
    }

    [Fact]
    public async Task JoinLobby_BadCode_Throws()
    {
        var token = await RegisterAsync("solo_" + Guid.NewGuid().ToString("N")[..8], "Solo");
        await using var conn = BuildConnection(token);
        await conn.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(() => conn.InvokeAsync("JoinLobby", "ZZZZZ"));
        Assert.Contains("No lobby", ex.Message);
    }

    // Phase 12 ANONYMITY: every seat in LobbyUpdated + GameStarted must carry a character
    // config — humans AND the injected AI — so a fully-customized lobby can't tell the AI
    // apart by its look. The AI has no saved character, so it gets the name-hash default.
    [Fact]
    public async Task StartGame_RosterAndLobby_CarryCharacterForEverySeat()
    {
        var conns = new List<HubConnection>();
        try
        {
            // Three players (min to start). One saves a custom character first.
            var host = _factory.CreateClient();
            var hostReg = await host.PostAsJsonAsync("/api/auth/register",
                new { username = "h_" + Guid.NewGuid().ToString("N")[..10], displayName = "Hostie", password = "Password123" });
            var hostBody = await hostReg.Content.ReadFromJsonAsync<AuthResponse>();
            host.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", hostBody!.Token);
            // Custom character for the host so at least one seat is non-default.
            // (outfit 4 = free set; premium ids need a reward, covered in AdminApiTests)
            var put = await host.PutAsync("/api/profile/character",
                new StringContent("{\"base\":5,\"hair\":2,\"outfit\":4,\"accessory\":1}",
                    System.Text.Encoding.UTF8, "application/json"));
            put.EnsureSuccessStatusCode();

            var hostConn = BuildConnection(hostBody.Token);
            var t2 = await RegisterAsync("g2_" + Guid.NewGuid().ToString("N")[..10], "Guest2");
            var t3 = await RegisterAsync("g3_" + Guid.NewGuid().ToString("N")[..10], "Guest3");
            var c2 = BuildConnection(t2);
            var c3 = BuildConnection(t3);
            conns.AddRange(new[] { hostConn, c2, c3 });

            CharLobby? hostLobby = null;
            List<CharRoster>? roster = null;
            hostConn.On<CharLobby>("LobbyUpdated", s => hostLobby = s);
            hostConn.On<List<CharRoster>>("GameStarted", r => roster = r);

            foreach (var c in conns) await c.StartAsync();
            await hostConn.InvokeAsync("CreateLobby");
            await WaitFor(() => hostLobby != null, "no LobbyUpdated");
            var code = hostLobby!.Code;
            await c2.InvokeAsync("JoinLobby", code);
            await c3.InvokeAsync("JoinLobby", code);
            await WaitFor(() => hostLobby!.Players.Count == 3, "not all 3 joined");

            // Every LobbyUpdated seat carries a character; the host's is the custom one.
            Assert.All(hostLobby!.Players, p => Assert.NotNull(p.Character));
            var hostSeat = hostLobby.Players.First(p => p.DisplayName == "Hostie");
            Assert.Equal(5, hostSeat.Character!.Base);
            Assert.Equal(4, hostSeat.Character.Outfit);

            await hostConn.InvokeAsync("StartGame");
            await WaitFor(() => roster != null, "no GameStarted");

            // Roster is 3 humans + the AI, and EVERY entry carries a character.
            Assert.Equal(4, roster!.Count);
            Assert.All(roster, e => Assert.NotNull(e.Character));

            // The AI seat (name read from the in-memory store, never off the wire) also
            // carries a character, and it's exactly the name-hash default of its fake name.
            var store = _factory.Services.GetRequiredService<LobbyStore>();
            var aiName = store.Get(code)!.AiDisplayName!;
            var aiSeat = roster.First(e => e.DisplayName == aiName);
            Assert.NotNull(aiSeat.Character);
            var expected = GameApi.Characters.CharacterDefaults.FromName(aiName);
            Assert.Equal(expected.Base, aiSeat.Character!.Base);
            Assert.Equal(expected.Hair, aiSeat.Character.Hair);
            Assert.Equal(expected.Outfit, aiSeat.Character.Outfit);
            Assert.Equal(expected.Accessory, aiSeat.Character.Accessory);
        }
        finally
        {
            foreach (var c in conns) await c.DisposeAsync();
        }
    }

    // --- helpers ---

    private async Task<string> RegisterAsync(string username, string displayName)
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username,
            displayName,
            password = "Password123"
        });
        if (!res.IsSuccessStatusCode)
            throw new Xunit.Sdk.XunitException(
                $"register failed {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var body = await res.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.Token;
    }

    private HubConnection BuildConnection(string token) =>
        new HubConnectionBuilder()
            .WithUrl(_factory.Server.BaseAddress + "hubs/game", options =>
            {
                // TestServer speaks HTTP long-polling over its in-memory handler.
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    private static async Task WaitFor(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(message);
    }

    private record AuthResponse(string Token, string DisplayName, string Username);
    private record LobbyState(string Code, string State, List<PlayerEntry> Players);
    private record PlayerEntry(string DisplayName, int TokensRemaining, bool IsConnected, bool IsHost);

    // Character-aware payload shapes for the phase-12 anonymity test.
    private record CharacterCfg(int Base, int Hair, int Outfit, int? Accessory);
    private record CharPlayer(string DisplayName, int TokensRemaining, bool IsConnected, bool IsHost, CharacterCfg Character);
    private record CharLobby(string Code, string State, List<CharPlayer> Players);
    private record CharRoster(string DisplayName, int TokensRemaining, CharacterCfg Character);
}
