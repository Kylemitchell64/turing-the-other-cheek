using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using GameApi.Lobbies;
using Xunit;
using Xunit.Abstractions;

namespace GameApi.Tests;

// Phase 3 PLAYTEST GATE. Four real SignalR clients drive a full game with the mock
// AI brain and compressed timings (see TestAppFactory GameTimings): join, answer
// rounds, a wrong accusation that burns a token, a veto flow (result hidden,
// cooldown round has no accusation window, vetoer priority window honored), and a
// correct accusation that ends the game as a Detector win.
//
// The AI's identity is hidden in every payload by design, so the test reads it out
// of the in-memory LobbyStore singleton (never off the wire) to know who to accuse.
public class PlaytestTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public PlaytestTests(TestAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    [Fact]
    public async Task FourClients_FullGame_VetoCooldownPriority_And_DetectorWin()
    {
        // --- four authed clients ---
        var names = new[] { "Alpha", "Bravo", "Charlie", "Delta" };
        var clients = new List<Player>();
        foreach (var n in names)
        {
            var token = await RegisterAsync($"{n.ToLower()}_{Guid.NewGuid():N}".Substring(0, 16), n);
            clients.Add(new Player(n, BuildConnection(token)));
        }

        try
        {
            foreach (var c in clients)
            {
                WireEvents(c);
                await c.Conn.StartAsync();
            }

            var host = clients[0];

            // Host creates, everyone else joins by code.
            await host.Conn.InvokeAsync("CreateLobby");
            await WaitUntil(() => host.LastLobby != null, "host never got LobbyUpdated");
            var code = host.LastLobby!.Code;

            foreach (var c in clients.Skip(1))
                await c.Conn.InvokeAsync("JoinLobby", code);

            await WaitUntil(() => host.LastLobby!.Players.Count == 4, "not all 4 joined");

            // --- start the game ---
            await host.Conn.InvokeAsync("StartGame");
            await WaitUntil(() => clients.All(c => c.Roster != null), "not everyone got GameStarted");
            Assert.All(clients, c => Assert.Equal(5, c.Roster!.Count)); // 4 humans + AI

            // Read the hidden AI identity from the in-memory store (NOT off the wire).
            var lobby = ResolveLobby(code);
            var aiName = lobby.AiDisplayName!;
            _out.WriteLine($"AI is playing as: {aiName}");
            Assert.Contains(host.Roster!, e => e.DisplayName == aiName);

            // Helper: which of our human clients is NOT the AI (all of them are human;
            // the AI has no client). Pick a human target that isn't the AI name.
            string HumanOtherThan(string me) =>
                names.First(n => n != me && n != aiName);

            var bravo = clients[1];
            var charlie = clients[2];
            var delta = clients[3];

            // ================= ROUND 1: everyone answers, no accusation =================
            await WaitUntil(() => clients.All(c => c.CurrentRound >= 1), "round 1 prompt never arrived");
            await AnswerAll(clients, "round one answer");
            await WaitUntil(() => clients.All(c => c.RevealsByRound.ContainsKey(1)), "round 1 reveal missing");
            Assert.All(clients, c => Assert.Equal(5, c.RevealsByRound[1].Answers.Count));

            // Let round 1's accusation window open, then pass with no accusation.
            await WaitUntil(() => host.WindowsByRound.ContainsKey(1), "no accusation window in round 1");
            await WaitUntil(() => clients.All(c => c.CurrentRound >= 2), "round 2 never started");

            // ================= ROUND 2: wrong accusation burns a token =================
            await AnswerAll(clients, "round two answer");
            await WaitUntil(() => clients.All(c => c.RevealsByRound.ContainsKey(2)), "round 2 reveal missing");
            await WaitUntil(() => host.WindowsByRound.ContainsKey(2), "no accusation window in round 2");

            // Bravo accuses a human who isn't the AI → wrong. Burns a Bravo token.
            var wrongTarget = HumanOtherThan("Bravo"); // a human, definitely not the AI
            var bravoTokensBefore = SeatTokens(code, "Bravo");
            host.ClearResolved();
            await bravo.Conn.InvokeAsync("MakeAccusation", wrongTarget);

            // Everyone sees AccusationMade; nobody vetoes → it resolves wrong.
            await WaitUntil(() => clients.All(c => c.LastAccusationMade != null), "AccusationMade not broadcast");
            await WaitUntil(() => host.LastResolved != null, "accusation never resolved");
            Assert.False(host.LastResolved!.Correct);
            await WaitUntil(() => SeatTokens(code, "Bravo") == bravoTokensBefore - 1, "Bravo's token wasn't burned");
            _out.WriteLine($"Bravo burned a token: {bravoTokensBefore} -> {SeatTokens(code, "Bravo")}");

            // ================= VETO FLOW =================
            // Next accusation window: Charlie accuses, Delta vetoes. Result hidden, the
            // following round has NO accusation window, the round after grants Delta priority.
            var vetoRound = await NextAccusationWindow(clients);
            _out.WriteLine($"Veto scenario accusation window opened in round {vetoRound}");

            host.ClearResolved();
            var charlieTarget = HumanOtherThan("Charlie");
            await charlie.Conn.InvokeAsync("MakeAccusation", charlieTarget);
            await WaitUntil(() => delta.VetoWindowDeadline != null, "Delta never got a veto window");

            var deltaTokensBefore = SeatTokens(code, "Delta");
            await delta.Conn.InvokeAsync("UseFakeOut");

            // FakeOutUsed fires to ALL; result is NEVER revealed.
            await WaitUntil(() => clients.All(c => c.LastFakeOutVetoer == "Delta"), "FakeOutUsed not broadcast to all");
            await Task.Delay(400); // give any (erroneous) resolve a chance to arrive
            Assert.Null(host.LastResolved); // the vetoed accusation's result stays hidden
            await WaitUntil(() => SeatTokens(code, "Delta") == deltaTokensBefore - 1, "Delta's veto token wasn't spent");

            // Cooldown round: exactly one full round with NO accusation window for anyone.
            var cooldownRound = vetoRound + 1;
            await WaitUntil(() => clients.All(c => c.CurrentRound >= cooldownRound), "cooldown round never started");
            await AnswerAll(clients, "cooldown round answer");
            await WaitUntil(() => clients.All(c => c.RevealsByRound.ContainsKey(cooldownRound)), "cooldown reveal missing");
            // Let the cooldown round fully play out before asserting no window opened.
            await WaitUntil(() => clients.All(c => c.CurrentRound > cooldownRound), "cooldown round didn't advance");
            Assert.False(host.WindowsByRound.ContainsKey(cooldownRound),
                $"cooldown round {cooldownRound} must have NO accusation window");
            _out.WriteLine($"Cooldown round {cooldownRound}: no accusation window (confirmed)");

            // Priority round: the round after the cooldown grants Delta an exclusive window.
            var priorityRound = cooldownRound + 1;
            await AnswerAll(clients, "priority round answer");
            await WaitUntil(() => host.WindowsByRound.TryGetValue(priorityRound, out var w) && w == "Delta",
                "Delta's priority window wasn't honored");
            _out.WriteLine($"Priority window granted to Delta in round {priorityRound}");

            // During the priority sub-window, a non-priority player can't accuse yet.
            var earlyEx = await Record.ExceptionAsync(() => clients[0].Conn.InvokeAsync("MakeAccusation", aiName));
            Assert.NotNull(earlyEx); // Alpha rejected during Delta's exclusive window

            // ================= CORRECT ACCUSATION → DETECTOR WIN =================
            // Delta (priority holder, and not the AI) correctly accuses the AI. No one
            // vetoes → Detector win + GameEnded with the full transcript + AI reveal.
            host.ClearResolved();
            await delta.Conn.InvokeAsync("MakeAccusation", aiName);
            await WaitUntil(() => clients.Any(c => c.LastAccusationMade != null), "correct AccusationMade missing");

            // Nobody vetoes → let the veto window lapse → resolution + game end.
            await WaitUntil(() => clients.All(c => c.GameEnded != null), "GameEnded never fired", timeoutSeconds: 15);

            var ended = delta.GameEnded!;
            Assert.Equal("Detector", ended.WinType);
            Assert.Equal("Delta", ended.WinnerName);
            Assert.Equal(aiName, ended.AiRealIdentityName);
            Assert.NotEmpty(ended.FullTranscript);
            Assert.Contains(ended.FullTranscript, m => m.IsAi && m.DisplayName == aiName);
            _out.WriteLine($"Detector win: Delta caught {aiName}. Transcript has {ended.FullTranscript.Count} messages.");
        }
        finally
        {
            foreach (var c in clients)
                await c.Conn.DisposeAsync();
        }
    }

    // --- scenario helpers ---

    // Submit an answer for every client for the current round (idempotent-ish: a
    // double submit just throws and is ignored).
    private static async Task AnswerAll(List<Player> clients, string text)
    {
        foreach (var c in clients)
        {
            try { await c.Conn.InvokeAsync("SubmitAnswer", $"{text} from {c.Name}"); }
            catch { /* already answered / window closed — fine for the flow */ }
        }
    }

    // Wait for the next accusation window to open (across clients) and return its round.
    private async Task<int> NextAccusationWindow(List<Player> clients)
    {
        var host = clients[0];
        var seen = host.AccusationWindowRound;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            // A new window in a round we haven't accused in yet.
            if (host.AccusationWindowRound > 0 && host.AccusationWindowRound != seen)
                return host.AccusationWindowRound;
            // Keep answering so rounds keep advancing until a window appears.
            await AnswerAll(clients, "auto answer");
            await Task.Delay(120);
        }
        throw new TimeoutException("no further accusation window opened");
    }

    private Lobby ResolveLobby(string code)
    {
        var store = _factory.Services.GetRequiredService<LobbyStore>();
        return store.Get(code) ?? throw new Xunit.Sdk.XunitException($"lobby {code} not in store");
    }

    private int SeatTokens(string code, string displayName)
    {
        var lobby = ResolveLobby(code);
        lock (lobby.Sync)
        {
            var p = lobby.Players.First(x => x.DisplayName == displayName);
            return p.TokensRemaining;
        }
    }

    // --- event wiring ---

    private static void WireEvents(Player c)
    {
        var conn = c.Conn;
        conn.On<LobbyState>("LobbyUpdated", s => c.LastLobby = s);
        conn.On<List<RosterEntry>>("GameStarted", r => c.Roster = r);
        conn.On<string, int, DateTime>("PromptStarted", (prompt, round, deadline) =>
        {
            c.CurrentRound = round;
            c.CurrentPrompt = prompt;
        });
        conn.On<Reveal>("AnswersRevealed", r =>
        {
            c.LastReveal = r;
            c.RevealsByRound[r.Round] = r;
        });
        conn.On<DateTime, string?>("AccusationWindowOpened", (deadline, priorityUserId) =>
        {
            c.AccusationWindowRound = c.CurrentRound;
            c.PriorityWindowUserId = priorityUserId;
            c.WindowsByRound[c.CurrentRound] = priorityUserId ?? "(general)";
        });
        conn.On<string, string>("AccusationMade", (accuser, accused) =>
            c.LastAccusationMade = (accuser, accused));
        conn.On<DateTime>("VetoWindowOpened", deadline => c.VetoWindowDeadline = deadline);
        conn.On<string>("FakeOutUsed", vetoer => c.LastFakeOutVetoer = vetoer);
        conn.On<bool, string, string>("AccusationResolved", (correct, accuser, accused) =>
            c.LastResolved = new Resolved(correct, accuser, accused));
        conn.On<string>("PlayerEliminated", name => c.LastEliminated = name);
        conn.On<GameEnded>("GameEnded", g => c.GameEnded = g);
    }

    // --- infra ---

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

    // --- test state holder ---

    private sealed class Player
    {
        public Player(string name, HubConnection conn) { Name = name; Conn = conn; }
        public string Name { get; }
        public HubConnection Conn { get; }

        public LobbyState? LastLobby;
        public List<RosterEntry>? Roster;
        public int CurrentRound;
        public string? CurrentPrompt;
        public Reveal? LastReveal;
        public readonly ConcurrentDictionary<int, Reveal> RevealsByRound = new();
        public int AccusationWindowRound;
        public string? PriorityWindowUserId;
        // round -> "(general)" or the priorityUserId. Tracks windows so we can assert
        // a specific round did / didn't get one even after later windows overwrite.
        public readonly ConcurrentDictionary<int, string> WindowsByRound = new();
        public (string Accuser, string Accused)? LastAccusationMade;
        public DateTime? VetoWindowDeadline;
        public string? LastFakeOutVetoer;
        public Resolved? LastResolved;
        public string? LastEliminated;
        public GameEnded? GameEnded;

        public void ClearResolved() => LastResolved = null;
    }

    private record AuthResponse(string Token, string DisplayName, string Username);
    private record LobbyState(string Code, string State, List<PlayerEntry> Players);
    private record PlayerEntry(string DisplayName, int TokensRemaining, bool IsConnected, bool IsHost);
    private record RosterEntry(string DisplayName, int TokensRemaining);
    private record RevealAnswer(string DisplayName, string Text);
    private record Reveal(int Round, string Prompt, List<RevealAnswer> Answers);
    private record Resolved(bool Correct, string Accuser, string Accused);
    private record TranscriptMessage(int Round, string DisplayName, string Text, bool IsAi);
    private record GameEnded(string WinType, string? WinnerName, string AiRealIdentityName, List<TranscriptMessage> FullTranscript);
}
