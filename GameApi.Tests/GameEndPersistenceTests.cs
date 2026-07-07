using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using GameApi.Data;
using GameApi.GameLoop;
using GameApi.Lobbies;
using GameApi.Models;
using Xunit;
using Xunit.Abstractions;

namespace GameApi.Tests;

// Phase 6 + 7 end-to-end against the real game-end persistence path:
//  - seeded StyleProfiles get injected into the AI system prompt (proved via a brain
//    that captures its AiTurnContext),
//  - each human's answers are harvested into WritingSamples (Source=Game),
//  - PlayerStats increments correctly for BOTH a detector win and an AI-survival game.
// Uses a dedicated factory with a capturing brain and a unique InMemory DB per run.
public class GameEndPersistenceTests
{
    private readonly ITestOutputHelper _out;
    public GameEndPersistenceTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task DetectorWin_SeedsStats_Harvests_And_InjectsSeededProfile()
    {
        using var factory = new PersistenceFactory();
        var names = new[] { "Alpha", "Bravo", "Charlie" };
        var (clients, userIds) = await ConnectAsync(factory, names);

        try
        {
            // Seed a style profile for Alpha BEFORE the game so it should be injected.
            SeedProfile(factory, userIds["Alpha"],
                "{\"avgLength\":22,\"capitalization\":\"lowercase\",\"slang\":[\"ngl\"]}");

            var host = clients[0];
            await host.Conn.InvokeAsync("CreateLobby");
            await WaitUntil(() => host.LastLobby != null, "no lobby");
            var code = host.LastLobby!.Code;
            foreach (var c in clients.Skip(1))
                await c.Conn.InvokeAsync("JoinLobby", code);
            await WaitUntil(() => host.LastLobby!.Players.Count == 3, "not all joined");

            await host.Conn.InvokeAsync("StartGame");
            await WaitUntil(() => clients.All(c => c.Roster != null), "no GameStarted");

            var lobby = ResolveLobby(factory, code);
            var aiName = lobby.AiDisplayName!;

            // Play one round of answers, then have a human correctly accuse the AI.
            await WaitUntil(() => clients.All(c => c.CurrentRound >= 1), "round 1 missing");
            await AnswerAll(clients, "my real answer");
            await WaitUntil(() => host.WindowsByRound.ContainsKey(1), "no accusation window");

            // Pick a human accuser who is not the AI (all our clients are human).
            var accuser = clients.First(c => c.Name != aiName);
            await accuser.Conn.InvokeAsync("MakeAccusation", aiName);
            await WaitUntil(() => clients.All(c => c.GameEnded != null), "game never ended", 15);

            Assert.Equal("Detector", accuser.GameEnded!.WinType);

            // The seeded profile must have reached the AI's turn context.
            await WaitUntil(() => PersistenceFactory.Brain.LastStyleSummaries != null, "brain never ran");
            Assert.Contains(PersistenceFactory.Brain.LastStyleSummaries!,
                s => s.StartsWith("Alpha:") && s.Contains("ngl"));

            // Give the async persist a moment (it runs off the engine loop).
            await WaitForDb(factory, db => db.Games.Any(g => g.JoinCode == code));

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();

            // Harvesting: every human who answered has a Game-source sample.
            foreach (var name in names)
            {
                var uid = userIds[name];
                Assert.True(db.WritingSamples.Any(s => s.UserId == uid && s.Source == SampleSource.Game),
                    $"{name} has no harvested game sample");
            }

            // Stats: 3 finishers each +1 GamesPlayed; the accuser +1 DetectorWin.
            foreach (var name in names)
            {
                var uid = userIds[name];
                var st = db.PlayerStats.Single(s => s.UserId == uid);
                Assert.Equal(1, st.GamesPlayed);
                Assert.Equal(0, st.AiSurvivalGamesWitnessed);
                Assert.Equal(0, st.TimesFooled);
            }
            var accuserStats = db.PlayerStats.Single(s => s.UserId == userIds[accuser.Name]);
            Assert.Equal(1, accuserStats.DetectorWins);
            _out.WriteLine($"Detector win by {accuser.Name}; stats + harvest + injection all verified.");
        }
        finally
        {
            foreach (var c in clients) await c.Conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task AiSurvival_IncrementsWitnessed_And_TimesFooled()
    {
        using var factory = new PersistenceFactory(maxRounds: 2);
        var names = new[] { "Dana", "Evan", "Faye" };
        var (clients, userIds) = await ConnectAsync(factory, names);

        try
        {
            var host = clients[0];
            await host.Conn.InvokeAsync("CreateLobby");
            await WaitUntil(() => host.LastLobby != null, "no lobby");
            var code = host.LastLobby!.Code;
            foreach (var c in clients.Skip(1))
                await c.Conn.InvokeAsync("JoinLobby", code);
            await WaitUntil(() => host.LastLobby!.Players.Count == 3, "not all joined");

            await host.Conn.InvokeAsync("StartGame");
            await WaitUntil(() => clients.All(c => c.Roster != null), "no GameStarted");

            var lobby = ResolveLobby(factory, code);
            var aiName = lobby.AiDisplayName!;

            // Round 1: Dana accuses a HUMAN (wrong) → burns a token, counts as fooled.
            await WaitUntil(() => clients.All(c => c.CurrentRound >= 1), "round 1 missing");
            await AnswerAll(clients, "answer one");
            await WaitUntil(() => host.WindowsByRound.ContainsKey(1), "no window r1");

            var dana = clients[0];
            var humanTarget = names.First(n => n != "Dana" && n != aiName);
            await dana.Conn.InvokeAsync("MakeAccusation", humanTarget);
            await WaitUntil(() => host.LastResolved is { Correct: false }, "wrong accusation didn't resolve", 15);

            // Let the game run out its 2-round cap with no correct accusation → AI survives.
            await WaitUntil(() => clients.All(c => c.GameEnded != null), "game never ended", 20);
            Assert.Equal("AiSurvival", host.GameEnded!.WinType);

            await WaitForDb(factory, db => db.Games.Any(g => g.JoinCode == code));
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();

            // Every finisher witnessed the escape; Dana (wrong accuser) was fooled once.
            foreach (var name in names)
            {
                var st = db.PlayerStats.Single(s => s.UserId == userIds[name]);
                Assert.Equal(1, st.GamesPlayed);
                Assert.Equal(1, st.AiSurvivalGamesWitnessed);
                Assert.Equal(0, st.DetectorWins);
            }
            var danaStats = db.PlayerStats.Single(s => s.UserId == userIds["Dana"]);
            Assert.Equal(1, danaStats.TimesFooled);
            var evanStats = db.PlayerStats.Single(s => s.UserId == userIds["Evan"]);
            Assert.Equal(0, evanStats.TimesFooled);
            _out.WriteLine("AI survival: witnessed + times-fooled increments verified.");
        }
        finally
        {
            foreach (var c in clients) await c.Conn.DisposeAsync();
        }
    }

    // --- helpers ---

    private static void SeedProfile(PersistenceFactory factory, string userId, string json)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        db.StyleProfiles.Add(new StyleProfile
        {
            UserId = userId,
            SummaryJson = json,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private async Task<(List<Player> clients, Dictionary<string, string> userIds)> ConnectAsync(
        PersistenceFactory factory, string[] names)
    {
        var clients = new List<Player>();
        var userIds = new Dictionary<string, string>();
        foreach (var n in names)
        {
            var (token, uid) = await RegisterAsync(factory, $"{n.ToLower()}_{Guid.NewGuid():N}"[..16], n);
            userIds[n] = uid;
            var c = new Player(n, BuildConnection(factory, token));
            WireEvents(c);
            await c.Conn.StartAsync();
            clients.Add(c);
        }
        return (clients, userIds);
    }

    private static async Task AnswerAll(List<Player> clients, string text)
    {
        foreach (var c in clients)
        {
            try { await c.Conn.InvokeAsync("SubmitAnswer", $"{text} from {c.Name}"); }
            catch { /* window closed / already answered */ }
        }
    }

    private static Lobby ResolveLobby(PersistenceFactory factory, string code)
    {
        var store = factory.Services.GetRequiredService<LobbyStore>();
        return store.Get(code) ?? throw new Xunit.Sdk.XunitException($"lobby {code} missing");
    }

    private static async Task WaitForDb(PersistenceFactory factory, Func<GameContext, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<GameContext>();
                if (predicate(db)) return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("DB condition never met");
    }

    private static void WireEvents(Player c)
    {
        var conn = c.Conn;
        conn.On<LobbyState>("LobbyUpdated", s => c.LastLobby = s);
        conn.On<List<RosterEntry>>("GameStarted", r => c.Roster = r);
        conn.On<string, int, DateTime>("PromptStarted", (prompt, round, _) => c.CurrentRound = round);
        conn.On<DateTime, string?>("AccusationWindowOpened", (_, priority) =>
            c.WindowsByRound[c.CurrentRound] = priority ?? "(general)");
        conn.On<bool, string, string>("AccusationResolved", (correct, accuser, accused) =>
            c.LastResolved = new Resolved(correct, accuser, accused));
        conn.On<GameEnded>("GameEnded", g => c.GameEnded = g);
    }

    private async Task<(string token, string userId)> RegisterAsync(
        PersistenceFactory factory, string username, string displayName)
    {
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register",
            new { username, displayName, password = "Password123" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<AuthResponse>();

        // Resolve the user id from the DB by username.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        var user = db.Users.Single(u => u.UserName == username);
        return (body!.Token, user.Id);
    }

    private static HubConnection BuildConnection(PersistenceFactory factory, string token) =>
        new HubConnectionBuilder()
            .WithUrl(factory.Server.BaseAddress + "hubs/game", options =>
            {
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    private static async Task WaitUntil(Func<bool> condition, string message, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(message);
    }

    private sealed class Player
    {
        public Player(string name, HubConnection conn) { Name = name; Conn = conn; }
        public string Name { get; }
        public HubConnection Conn { get; }
        public LobbyState? LastLobby;
        public List<RosterEntry>? Roster;
        public int CurrentRound;
        public readonly ConcurrentDictionary<int, string> WindowsByRound = new();
        public Resolved? LastResolved;
        public GameEnded? GameEnded;
    }

    private record AuthResponse(string Token, string DisplayName, string Username);
    private record LobbyState(string Code, string State, List<PlayerEntry> Players);
    private record PlayerEntry(string DisplayName, int TokensRemaining, bool IsConnected, bool IsHost);
    private record RosterEntry(string DisplayName, int TokensRemaining);
    private record Resolved(bool Correct, string Accuser, string Accused);
    private record TranscriptMessage(int Round, string DisplayName, string Text, bool IsAi);
    private record GameEnded(string WinType, string? WinnerName, string AiRealIdentityName, List<TranscriptMessage> FullTranscript);
}

// A factory that swaps in a capturing brain (records the last AiTurnContext so tests
// can assert style-summary injection) and a fresh InMemory DB per instance.
public class PersistenceFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
{
    public static CapturingBrain Brain { get; } = new();
    private readonly string _dbName = "persist-" + Guid.NewGuid().ToString("N");
    private readonly int _maxRounds;

    public PersistenceFactory(int maxRounds = 8) => _maxRounds = maxRounds;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable(
            "JWT_KEY", "test-only-signing-key-that-is-at-least-64-characters-long-000000000");
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-only-signing-key-that-is-at-least-64-characters-long-000000000",
                ["Ai:Brain"] = "Mock",
                ["RateLimit:PermitsPerMinute"] = "1000",
                ["GameTimings:PromptSeconds"] = "1",
                ["GameTimings:RevealSeconds"] = "1",
                ["GameTimings:AccusationSeconds"] = "3",
                ["GameTimings:VetoSeconds"] = "2",
                ["GameTimings:PrioritySeconds"] = "2",
                ["GameTimings:TickMilliseconds"] = "100",
                ["GameTimings:MaxRounds"] = _maxRounds.ToString()
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<GameContext>>();
            services.RemoveAll<GameContext>();
            services.AddDbContext<GameContext>(o => o.UseInMemoryDatabase(_dbName));

            // Swap the brain for the capturing one so we can inspect injected summaries.
            services.RemoveAll<IAiBrain>();
            services.AddSingleton<IAiBrain>(Brain);
        });
    }
}

// Wraps MockBrain's behavior but records the AiTurnContext it was last called with.
public class CapturingBrain : IAiBrain
{
    private readonly MockBrain _inner = new();
    public IReadOnlyList<string>? LastStyleSummaries { get; private set; }

    public Task<AiAnswer> AnswerAsync(AiTurnContext context, CancellationToken ct)
    {
        LastStyleSummaries = context.StyleSummaries;
        return _inner.AnswerAsync(context, ct);
    }
}
