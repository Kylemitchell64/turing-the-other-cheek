using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.SignalR;
using GameApi.Data;
using GameApi.GameLoop;
using GameApi.Lobbies;
using GameApi.Models;
using Xunit;
using Xunit.Abstractions;

namespace GameApi.Tests;

// Phase 22 REVERSE mode. Unit coverage for the mode keys, the accuracy→winType math, and
// the guesser's JSON parse + random fallback; plus end-to-end coverage of the start gating
// (guests / thin profiles rejected) and a full 6-round reverse game driven by four real
// SignalR clients with the MockGuesser (which always fingers the first player, so the
// accuracy and TimesReadByAi outcomes are deterministic).
public class ReverseModeTests
{
    private readonly ITestOutputHelper _out;
    public ReverseModeTests(ITestOutputHelper output) => _out = output;

    // ---- unit: mode keys ----

    [Fact]
    public void GameModes_ValidatesKeys_AndDetectsReverse()
    {
        Assert.True(GameModes.IsValidKey("classic"));
        Assert.True(GameModes.IsValidKey("reverse"));
        Assert.False(GameModes.IsValidKey("sideways"));
        Assert.False(GameModes.IsValidKey(null));
        Assert.Equal("classic", GameModes.DefaultKey);

        Assert.True(GameModes.IsReverse("reverse"));
        Assert.False(GameModes.IsReverse("classic"));
        Assert.False(GameModes.IsReverse(null));
    }

    // ---- unit: accuracy → winType, both sides of 50% ----

    [Theory]
    [InlineData(3, 6, true)]   // exactly half → AI's side
    [InlineData(4, 6, true)]
    [InlineData(6, 6, true)]
    [InlineData(2, 6, false)]  // under half → humans hid
    [InlineData(0, 6, false)]
    [InlineData(0, 0, false)]  // no data → humans hid (defensive)
    public void ReverseAiWon_SplitsAtHalf(int correct, int total, bool aiWon)
    {
        Assert.Equal(aiWon, GameEngine.ReverseAiWon(correct, total));
    }

    // ---- unit: guesser JSON parse + random fallback ----

    [Fact]
    public void ParseGuesses_MapsValidJson_ResolvingNamesCaseInsensitively()
    {
        var answers = new List<AnonAnswer> { new("a", "olives never again"), new("b", "coffee then chaos") };
        var names = new List<string> { "Sam", "Jo" };
        var raw = "```json\n{\"a\":{\"name\":\"sam\",\"taunt\":\"the lowercase gives you away\"},\"b\":{\"name\":\"Jo\",\"taunt\":\"too wholesome\"}}\n```";

        var result = GeminiGuesser.ParseGuesses(raw, answers, names);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Guesses.Count);
        var a = result.Guesses.Single(g => g.AnswerId == "a");
        Assert.Equal("Sam", a.GuessedName); // resolved back to canonical casing
        Assert.Contains("lowercase", a.Taunt);
    }

    [Fact]
    public void ParseGuesses_ReturnsNull_OnUnknownNameOrGarbage()
    {
        var answers = new List<AnonAnswer> { new("a", "x"), new("b", "y") };
        var names = new List<string> { "Sam", "Jo" };

        // A name that isn't in the roster → null (engine will random-fall-back).
        Assert.Null(GeminiGuesser.ParseGuesses(
            "{\"a\":{\"name\":\"Sam\"},\"b\":{\"name\":\"Nobody\"}}", answers, names));
        // Missing an answer id → null.
        Assert.Null(GeminiGuesser.ParseGuesses("{\"a\":{\"name\":\"Sam\"}}", answers, names));
        // Not JSON at all → null.
        Assert.Null(GeminiGuesser.ParseGuesses("sorry, I can't help with that", answers, names));
        Assert.Null(GeminiGuesser.ParseGuesses(null, answers, names));
    }

    [Fact]
    public void RandomFallback_AlwaysReturnsFullValidMapping()
    {
        var answers = new List<AnonAnswer> { new("a", "1"), new("b", "2"), new("c", "3") };
        var names = new List<string> { "Sam", "Jo", "Kai" };
        var rng = new Random(42);

        for (var i = 0; i < 50; i++)
        {
            var result = GeminiGuesser.RandomFallback(answers, names, rng);
            Assert.Equal(3, result.Guesses.Count);
            Assert.All(result.Guesses, g => Assert.Contains(g.GuessedName, names));
            Assert.All(result.Guesses, g => Assert.False(string.IsNullOrWhiteSpace(g.Taunt)));
            // Every answer id covered exactly once.
            Assert.Equal(new[] { "a", "b", "c" }, result.Guesses.Select(g => g.AnswerId).OrderBy(x => x).ToArray());
        }
    }

    // ---- integration: start gating ----

    [Fact]
    public async Task Reverse_Start_RejectsGuestsAndThinProfiles_ByName()
    {
        using var factory = new ReverseFactory();
        // Ready: registered + seeded. Thin: registered, no samples. Guesty: a guest, seeded
        // (still rejected — guests can't play reverse regardless of history).
        var (host, hostId) = await ConnectRegisteredAsync(factory, "Ready");
        var (thin, thinId) = await ConnectRegisteredAsync(factory, "Thin");
        var (guest, guestId) = await ConnectGuestAsync(factory, "Guesty");
        var clients = new[] { host, thin, guest }.ToList();

        try
        {
            SeedSamples(factory, hostId, 3);
            SeedSamples(factory, guestId, 5); // history doesn't save a guest

            // A guest's server display name is its username, not the friendly label we passed.
            string guestName = "";
            using (var scope = factory.Services.CreateScope())
                guestName = scope.ServiceProvider.GetRequiredService<GameContext>()
                    .Users.Single(u => u.Id == guestId).DisplayName ?? "";

            await host.Conn.InvokeAsync("CreateLobby");
            await WaitUntil(() => host.LastLobby != null, "no lobby");
            var code = host.LastLobby!.Code;
            await thin.Conn.InvokeAsync("JoinLobby", code);
            await guest.Conn.InvokeAsync("JoinLobby", code);
            await WaitUntil(() => host.LastLobby!.Players.Count == 3, "not all joined");

            await host.Conn.InvokeAsync("SetLobbyOptions", "family", "normal", "standard", "reverse");

            var ex = await Assert.ThrowsAsync<HubException>(() => host.Conn.InvokeAsync("StartGame"));
            _out.WriteLine(ex.Message);
            Assert.Contains("not ready", ex.Message);
            Assert.Contains("Thin", ex.Message);        // thin profile flagged
            Assert.Contains(guestName, ex.Message);     // guest flagged even though seeded
            Assert.DoesNotContain("Ready", ex.Message); // the seeded, signed-in host is fine
        }
        finally
        {
            foreach (var c in clients) await c.Conn.DisposeAsync();
        }
    }

    // ---- integration: full reverse game ----

    [Fact]
    public async Task Reverse_FullGame_EmitsRevealAndGuesses_EndsHumansHidden_IncrementsTimesReadByAi()
    {
        using var factory = new ReverseFactory();
        var names = new[] { "Ana", "Ben", "Cy" };
        var clients = new List<Player>();
        var userIds = new Dictionary<string, string>();
        foreach (var n in names)
        {
            var (c, uid) = await ConnectRegisteredAsync(factory, n);
            clients.Add(c);
            userIds[n] = uid;
            SeedSamples(factory, uid, 3); // everyone signed in with history → reverse-ready
        }

        try
        {
            var host = clients[0];
            await host.Conn.InvokeAsync("CreateLobby");
            await WaitUntil(() => host.LastLobby != null, "no lobby");
            var code = host.LastLobby!.Code;
            foreach (var c in clients.Skip(1))
                await c.Conn.InvokeAsync("JoinLobby", code);
            await WaitUntil(() => host.LastLobby!.Players.Count == 3, "not all joined");

            await host.Conn.InvokeAsync("SetLobbyOptions", "family", "normal", "standard", "reverse");
            await WaitUntil(() => clients.All(c => c.LastMode == "reverse"), "mode didn't propagate");

            await host.Conn.InvokeAsync("StartGame");
            await WaitUntil(() => clients.All(c => c.Roster != null), "no GameStarted");
            // Reverse has NO hidden AI seat — the roster is just the humans.
            Assert.All(clients, c => Assert.Equal(3, c.Roster!.Count));

            // Drive all 6 rounds: answer, then confirm both reverse events land per round.
            for (var round = 1; round <= 6; round++)
            {
                await WaitUntil(() => clients.All(c => c.CurrentRound >= round), $"round {round} prompt missing", 20);
                await AnswerAll(clients, $"answer r{round}");
                await WaitUntil(() => clients.All(c => c.RevealStartedRounds.Contains(round)),
                    $"round {round} ReverseRevealStarted missing", 20);
                await WaitUntil(() => clients.All(c => c.GuessesByRound.ContainsKey(round)),
                    $"round {round} AiGuessesRevealed missing", 20);

                // The shuffled reveal is anonymous: ids a/b/c, one per seat, no names.
                var reveal = host.RevealAnswersByRound[round];
                Assert.Equal(3, reveal.Count);
                Assert.Equal(new[] { "a", "b", "c" }, reveal.Select(a => a.Id).OrderBy(x => x).ToArray());

                // MockGuesser fingers the first player every time, so exactly ONE answer per
                // round (that player's own) comes back correct.
                var g = host.GuessesByRound[round];
                Assert.Equal(3, g.Guesses.Count);
                Assert.Equal(1, g.RoundCorrect);
                Assert.All(g.Guesses, x => Assert.Equal("Ana", x.GuessedName));
            }

            await WaitUntil(() => clients.All(c => c.GameEnded != null), "game never ended", 20);

            // 6 correct out of 18 attributions = 33% < 50% → the humans stayed hidden.
            var ended = host.GameEnded!;
            Assert.Equal("HumansHidden", ended.WinType);

            await WaitForDb(factory, db => db.Games.Any(g => g.JoinCode == code));
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();

            // Ana (always guessed) was read once per round; the others never were.
            Assert.Equal(6, db.PlayerStats.Single(s => s.UserId == userIds["Ana"]).TimesReadByAi);
            Assert.Equal(0, db.PlayerStats.Single(s => s.UserId == userIds["Ben"]).TimesReadByAi);
            Assert.Equal(0, db.PlayerStats.Single(s => s.UserId == userIds["Cy"]).TimesReadByAi);
            // Everyone still gets a GamesPlayed bump like any finished game.
            Assert.All(names, n => Assert.Equal(1, db.PlayerStats.Single(s => s.UserId == userIds[n]).GamesPlayed));
            _out.WriteLine("Reverse game: events, HumansHidden, and TimesReadByAi all verified.");
        }
        finally
        {
            foreach (var c in clients) await c.Conn.DisposeAsync();
        }
    }

    // --- helpers ---

    private static void SeedSamples(ReverseFactory factory, string userId, int count)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        for (var i = 0; i < count; i++)
            db.WritingSamples.Add(new WritingSample
            {
                UserId = userId,
                Text = $"seeded sample {i} with enough words to look like real writing",
                Source = SampleSource.Upload,
                CreatedAt = DateTime.UtcNow.AddMinutes(-i)
            });
        db.SaveChanges();
    }

    private async Task<(Player client, string userId)> ConnectRegisteredAsync(ReverseFactory factory, string name)
    {
        var username = $"{name.ToLower()}_{Guid.NewGuid():N}"[..16];
        var http = factory.CreateClient();
        var res = await http.PostAsJsonAsync("/api/auth/register",
            new { username, displayName = name, password = "Password123" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<AuthResponse>();
        return await FinishConnectAsync(factory, name, username, body!.Token);
    }

    private async Task<(Player client, string userId)> ConnectGuestAsync(ReverseFactory factory, string name)
    {
        var username = $"{name.ToLower()}_{Guid.NewGuid():N}"[..16];
        var http = factory.CreateClient();
        var res = await http.PostAsJsonAsync("/api/auth/guest", new { username });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<AuthResponse>();
        return await FinishConnectAsync(factory, name, username, body!.Token);
    }

    private async Task<(Player, string)> FinishConnectAsync(ReverseFactory factory, string name, string username, string token)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        var uid = db.Users.Single(u => u.UserName == username).Id;

        var c = new Player(name, BuildConnection(factory, token));
        WireEvents(c);
        await c.Conn.StartAsync();
        return (c, uid);
    }

    private static async Task AnswerAll(List<Player> clients, string text)
    {
        foreach (var c in clients)
        {
            try { await c.Conn.InvokeAsync("SubmitAnswer", $"{text} from {c.Name}"); }
            catch { /* window closed / already answered */ }
        }
    }

    private static void WireEvents(Player c)
    {
        var conn = c.Conn;
        conn.On<LobbyState>("LobbyUpdated", s => c.LastLobby = s);
        conn.On<List<RosterEntry>>("GameStarted", r => c.Roster = r);
        conn.On<string, string, string, string?, string>("LobbyOptionsChanged", (_, _, _, _, mode) => c.LastMode = mode);
        conn.On<string, int, DateTime>("PromptStarted", (_, round, _) => c.CurrentRound = round);
        conn.On<ReverseRevealStarted>("ReverseRevealStarted", r =>
        {
            c.RevealStartedRounds.Add(r.Round);
            c.RevealAnswersByRound[r.Round] = r.Answers;
        });
        conn.On<AiGuessesRevealed>("AiGuessesRevealed", g => c.GuessesByRound[g.Round] = g);
        conn.On<GameEnded>("GameEnded", g => c.GameEnded = g);
    }

    private static HubConnection BuildConnection(ReverseFactory factory, string token) =>
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

    private static async Task WaitForDb(ReverseFactory factory, Func<GameContext, bool> predicate)
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

    // --- test state + payload records ---

    private sealed class Player
    {
        public Player(string name, HubConnection conn) { Name = name; Conn = conn; }
        public string Name { get; }
        public HubConnection Conn { get; }
        public LobbyState? LastLobby;
        public List<RosterEntry>? Roster;
        public int CurrentRound;
        public string? LastMode;
        public readonly ConcurrentBag<int> RevealStartedRounds = new();
        public readonly ConcurrentDictionary<int, List<AnonRec>> RevealAnswersByRound = new();
        public readonly ConcurrentDictionary<int, AiGuessesRevealed> GuessesByRound = new();
        public GameEnded? GameEnded;
    }

    private record AuthResponse(string Token, string DisplayName, string Username);
    private record LobbyState(string Code, string State, List<PlayerEntry> Players, string PackKey,
        string Difficulty, string PaceKey, string? CrewName, string? CustomPackName, string MusicMood, string Mode);
    private record PlayerEntry(string DisplayName, int TokensRemaining, bool IsConnected, bool IsHost);
    private record RosterEntry(string DisplayName, int TokensRemaining);
    private record AnonRec(string Id, string Text);
    private record ReverseRevealStarted(int Round, string Prompt, List<AnonRec> Answers);
    private record Guess(string AnswerId, string GuessedName, bool Correct, string ActualName, string Taunt);
    private record AiGuessesRevealed(int Round, List<Guess> Guesses, int RoundCorrect, int RoundTotal, int GameCorrect, int GameTotal);
    private record TranscriptMessage(int Round, string DisplayName, string Text, bool IsAi);
    private record GameEnded(string WinType, string? WinnerName, string AiRealIdentityName, List<TranscriptMessage> FullTranscript);
}

// Reverse-mode factory: fresh InMemory DB per run, mock brain + mock guesser (the default
// when Ai:Brain=Mock), and short reverse-friendly timings so six rounds run in seconds.
public class ReverseFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
{
    private readonly string _dbName = "reverse-" + Guid.NewGuid().ToString("N");

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
                ["RateLimit:PermitsPerMinute"] = "100000",
                ["GameTimings:PromptSeconds"] = "1",
                ["GameTimings:RevealSeconds"] = "1",
                ["GameTimings:AccusationSeconds"] = "2",
                ["GameTimings:VetoSeconds"] = "2",
                ["GameTimings:PrioritySeconds"] = "2",
                ["GameTimings:TickMilliseconds"] = "100",
                ["GameTimings:MaxRounds"] = "8"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<GameContext>>();
            services.RemoveAll<GameContext>();
            services.AddDbContext<GameContext>(o => o.UseInMemoryDatabase(_dbName));
        });
    }
}
