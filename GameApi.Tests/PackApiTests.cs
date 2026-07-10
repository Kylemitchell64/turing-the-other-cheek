using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using GameApi.Data;
using GameApi.GameLoop;
using GameApi.Lobbies;
using Xunit;

namespace GameApi.Tests;

// Phase 20 — the /api/packs endpoints and the SetCustomPack hub method, with a stubbed
// AI provider so generation outcomes are deterministic. Covers: generate 200 / REFUSED
// 422 / wordlist 422, decode round-trip + tamper, SetCustomPack host-only + the game
// drawing from the custom pack, and crew custom-pack persist + restore.
public class PackApiTests : IClassFixture<PackApiTests.PackFactory>
{
    private readonly PackFactory _factory;
    public PackApiTests(PackFactory factory) => _factory = factory;

    // A settable stub for the AI chain.
    public sealed class StubAi : IAiTextProvider
    {
        public string? Next { get; set; }
        public bool HasKey => true;
        public Task<string?> GenerateAsync(string s, string u, double t, int m, CancellationToken ct)
            => Task.FromResult(Next);
    }

    public class PackFactory : TestAppFactory
    {
        public StubAi Ai { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAiTextProvider>();
                services.AddSingleton<IAiTextProvider>(Ai);
            });
        }
    }

    // ---- generate ----

    [Fact]
    public async Task Generate_CleanTheme_Returns200WithCode()
    {
        _factory.Ai.Next =
            "NAME: Cartoon Chaos\nNSFW: false\nthe theme song you know by heart\nbest saturday show\nworst villain ever";
        var client = await UserClient();

        var res = await client.PostAsJsonAsync("/api/packs/generate", new { theme = "90s cartoons", count = 20 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<PackResponse>();
        Assert.Equal("Cartoon Chaos", body!.Name);
        Assert.False(body.Nsfw);
        Assert.NotEmpty(body.Prompts);
        Assert.StartsWith("TTOC1.", body.Code);
    }

    [Fact]
    public async Task Generate_Refused_Returns422()
    {
        _factory.Ai.Next = "REFUSED";
        var client = await UserClient();
        var res = await client.PostAsJsonAsync("/api/packs/generate", new { theme = "something against the rules" });
        Assert.Equal(422, (int)res.StatusCode);
    }

    [Fact]
    public async Task Generate_WordlistHit_Returns422()
    {
        _factory.Ai.Next = "NAME: Edgy\nNSFW: true\na fine line\nyou absolute retard\nanother fine line";
        var client = await UserClient();
        var res = await client.PostAsJsonAsync("/api/packs/generate", new { theme = "edgy roast humor" });
        Assert.Equal(422, (int)res.StatusCode);
    }

    [Fact]
    public async Task Generate_ShortTheme_Returns400()
    {
        var client = await UserClient();
        var res = await client.PostAsJsonAsync("/api/packs/generate", new { theme = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- decode ----

    [Fact]
    public async Task Decode_RoundTrips_AndRejectsTamper()
    {
        var codec = _factory.Services.GetRequiredService<PackCodec>();
        var code = codec.Encode(new CustomPack("Decode Me", false, new[] { "one", "two", "three" }));
        var client = await UserClient();

        var ok = await client.PostAsJsonAsync("/api/packs/decode", new { code });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<PackResponse>();
        Assert.Equal("Decode Me", body!.Name);
        Assert.Equal(3, body.Prompts.Length);

        // Tamper the body → 400.
        var parts = code.Split('.');
        var b = parts[1].ToCharArray();
        b[0] = b[0] == 'A' ? 'B' : 'A';
        var tampered = parts[0] + "." + new string(b) + "." + parts[2];
        var bad = await client.PostAsJsonAsync("/api/packs/decode", new { code = tampered });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // ---- hub: SetCustomPack host-only + the game draws from it ----

    [Fact]
    public async Task SetCustomPack_HostOnly_AndGameDrawsFromIt()
    {
        var codec = _factory.Services.GetRequiredService<PackCodec>();
        var prompts = new[] { "custom-alpha", "custom-beta", "custom-gamma" };
        var code = codec.Encode(new CustomPack("Room Pack", false, prompts));

        var host = await UserToken();
        var g1 = await UserToken();
        var g2 = await UserToken();

        await using var hostConn = Conn(host);
        await using var c1 = Conn(g1);
        await using var c2 = Conn(g2);

        LobbyState? hostState = null;
        string? seenPack = null;
        string? seenCustom = null;
        string? firstPrompt = null;
        hostConn.On<LobbyState>("LobbyUpdated", s => hostState = s);
        hostConn.On<string, string, string, string?, string>("LobbyOptionsChanged", (pack, _, _, custom, _) => { seenPack = pack; seenCustom = custom; });
        hostConn.On<string, int, DateTime>("PromptStarted", (p, _, _2) => firstPrompt ??= p);

        await hostConn.StartAsync();
        await c1.StartAsync();
        await c2.StartAsync();

        await hostConn.InvokeAsync("CreateLobby");
        await WaitFor(() => hostState != null, "no lobby");
        var lobbyCode = hostState!.Code;
        await c1.InvokeAsync("JoinLobby", lobbyCode);
        await c2.InvokeAsync("JoinLobby", lobbyCode);
        await WaitFor(() => hostState!.Players.Count == 3, "not all joined");

        // A non-host can't set the custom pack.
        var ex = await Assert.ThrowsAsync<HubException>(() => c1.InvokeAsync("SetCustomPack", code));
        Assert.Contains("host", ex.Message);

        // A garbage code is rejected with the tamper message.
        var badEx = await Assert.ThrowsAsync<HubException>(() => hostConn.InvokeAsync("SetCustomPack", "TTOC1.nope.nope"));
        Assert.Contains("messed with", badEx.Message);

        // The host installs it → LobbyOptionsChanged carries packKey=custom + the name.
        await hostConn.InvokeAsync("SetCustomPack", code);
        await WaitFor(() => seenCustom == "Room Pack", "custom name not broadcast");
        Assert.Equal("custom", seenPack);

        // The game draws its prompts from the custom pack.
        await hostConn.InvokeAsync("StartGame");
        await WaitFor(() => firstPrompt != null, "no PromptStarted");
        Assert.Contains(firstPrompt, prompts);
    }

    // ---- crew: setting a custom pack in a crew lobby persists to the crew row ----

    [Fact]
    public async Task CrewCustomPack_PersistsToCrewRow()
    {
        var codec = _factory.Services.GetRequiredService<PackCodec>();
        var code = codec.Encode(new CustomPack("Crew Pack", false, new[] { "crew-one", "crew-two" }));

        var owner = await UserClient();
        var crew = await CreateCrew(owner, "Persist Crew " + Guid.NewGuid().ToString("N")[..4]);
        var ownerTok = owner.DefaultRequestHeaders.Authorization!.Parameter!;

        await using var conn = Conn(ownerTok);
        LobbyState? state = null;
        conn.On<LobbyState>("LobbyUpdated", s => state = s);
        await conn.StartAsync();
        await conn.InvokeAsync("CreateCrewLobby", crew.Id);
        await WaitFor(() => state != null, "no crew lobby");
        await conn.InvokeAsync("SetCustomPack", code);

        // Fire-and-forget persist — poll the crew row until it reflects the custom pack.
        await WaitForDb(db =>
        {
            var row = db.Crews.FirstOrDefault(c => c.Id == crew.Id);
            return row is { PackKey: "custom" } && !string.IsNullOrEmpty(row.CustomPackCode);
        }, "crew custom pack never persisted");

        // And picking a normal pack afterwards clears the saved code.
        await conn.InvokeAsync("SetLobbyOptions", "trivia", "normal", "standard", "classic");
        await WaitForDb(db =>
        {
            var row = db.Crews.FirstOrDefault(c => c.Id == crew.Id);
            return row is { PackKey: "trivia", CustomPackCode: null };
        }, "crew custom pack code never cleared");
    }

    // ---- crew: a saved custom pack is restored onto a fresh crew lobby + the game uses it ----

    [Fact]
    public async Task CrewCustomPack_RestoresOntoFreshLobby()
    {
        var codec = _factory.Services.GetRequiredService<PackCodec>();
        var prompts = new[] { "crew-alpha", "crew-beta", "crew-gamma" };
        var code = codec.Encode(new CustomPack("Saved Pack", false, prompts));

        var owner = await UserClient();
        var crew = await CreateCrew(owner, "Restore Crew " + Guid.NewGuid().ToString("N")[..4]);

        // Seed the crew row directly (no prior live lobby exists), so opening a lobby must
        // restore the pack straight from the DB.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();
            var row = db.Crews.First(c => c.Id == crew.Id);
            row.PackKey = "custom";
            row.CustomPackCode = code;
            db.SaveChanges();
        }

        var m1 = await UserClient();
        var m2 = await UserClient();
        await JoinCrew(m1, crew.JoinCode);
        await JoinCrew(m2, crew.JoinCode);

        await using var oc = Conn(owner.DefaultRequestHeaders.Authorization!.Parameter!);
        await using var c1 = Conn(m1.DefaultRequestHeaders.Authorization!.Parameter!);
        await using var c2 = Conn(m2.DefaultRequestHeaders.Authorization!.Parameter!);

        LobbyState? os = null;
        string? firstPrompt = null;
        oc.On<LobbyState>("LobbyUpdated", s => os = s);
        oc.On<string, int, DateTime>("PromptStarted", (p, _, _2) => firstPrompt ??= p);
        await oc.StartAsync();
        await c1.StartAsync();
        await c2.StartAsync();

        await oc.InvokeAsync("CreateCrewLobby", crew.Id);
        await WaitFor(() => os != null, "no restored crew lobby");
        Assert.Equal("custom", os!.PackKey);
        Assert.Equal("Saved Pack", os.CustomPackName);

        var liveCode = os.Code;
        await c1.InvokeAsync("JoinLobby", liveCode);
        await c2.InvokeAsync("JoinLobby", liveCode);
        await WaitFor(() => os!.Players.Count == 3, "not all crew members joined");

        await oc.InvokeAsync("StartGame");
        await WaitFor(() => firstPrompt != null, "no PromptStarted");
        Assert.Contains(firstPrompt, prompts);
    }

    // ---- helpers ----

    private async Task<HttpClient> UserClient()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "u_" + Guid.NewGuid().ToString("N")[..10],
            displayName = "User" + Guid.NewGuid().ToString("N")[..4],
            password = "Password123"
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<AuthDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return client;
    }

    private async Task<string> UserToken()
    {
        var client = await UserClient();
        return client.DefaultRequestHeaders.Authorization!.Parameter!;
    }

    private static async Task<CrewDto> CreateCrew(HttpClient client, string name)
    {
        var res = await client.PostAsJsonAsync("/api/crews", new { name });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<CrewDto>())!;
    }

    private static async Task JoinCrew(HttpClient client, string code)
    {
        var res = await client.PostAsJsonAsync("/api/crews/join", new { code });
        res.EnsureSuccessStatusCode();
    }

    private HubConnection Conn(string token) =>
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

    private async Task WaitForDb(Func<GameContext, bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<GameContext>();
                if (condition(db)) return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException(message);
    }

    private record AuthDto(string Token, string DisplayName, string Username);
    private record CrewDto(int Id, string Name, string JoinCode, string PackKey, string Difficulty,
        string PaceKey, int MemberCount, int GamesPlayed, bool IsOwner);
    private record PackResponse(string Name, bool Nsfw, string[] Prompts, string Code);
    private record LobbyState(string Code, string State, List<PlayerEntry> Players,
        string PackKey, string Difficulty, string PaceKey, string? CrewName, string? CustomPackName);
    private record PlayerEntry(string DisplayName, int TokensRemaining, bool IsConnected, bool IsHost);
}
