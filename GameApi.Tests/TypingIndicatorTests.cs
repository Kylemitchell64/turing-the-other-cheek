using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using GameApi.Lobbies;
using Xunit;
using Xunit.Abstractions;

namespace GameApi.Tests;

// Phase 11 typing indicators. Proves the anonymity-critical bits end-to-end with real
// SignalR clients + the mock brain: the AI fakes a typing indicator (PlayerTyping with
// its name, arriving before it answers), humans' typing broadcasts to the group, and
// SetTyping does nothing outside Prompting. The PlayerTyping payload is (name, bool)
// for AI and humans alike — nothing in it distinguishes the machine.
public class TypingIndicatorTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public TypingIndicatorTests(TestAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    [Fact]
    public async Task AiFakesTyping_HumansBroadcast_AndTypingIsPromptingOnly()
    {
        var names = new[] { "Alpha", "Bravo", "Charlie" };
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
            var bravo = clients[1];

            await host.Conn.InvokeAsync("CreateLobby");
            await WaitUntil(() => host.LastLobby != null, "host never got LobbyUpdated");
            var code = host.LastLobby!.Code;

            foreach (var c in clients.Skip(1))
                await c.Conn.InvokeAsync("JoinLobby", code);
            await WaitUntil(() => host.LastLobby!.Players.Count == 3, "not all 3 joined");

            // ---- GUARD: SetTyping in the Lobby state (not Prompting) broadcasts nothing.
            var beforeStart = TotalTyping(clients);
            await bravo.Conn.InvokeAsync("SetTyping", true);
            await Task.Delay(400);
            Assert.Equal(beforeStart, TotalTyping(clients));
            _out.WriteLine("SetTyping outside Prompting produced no PlayerTyping (guard ok)");

            // ---- start the game ----
            await host.Conn.InvokeAsync("StartGame");
            await WaitUntil(() => clients.All(c => c.Roster != null), "not everyone got GameStarted");

            var lobby = ResolveLobby(code);
            var aiName = lobby.AiDisplayName!;
            _out.WriteLine($"AI is playing as: {aiName}");

            await WaitUntil(() => clients.All(c => c.CurrentRound >= 1), "round 1 prompt never arrived");

            // ---- HUMAN typing broadcasts to the group ----
            await bravo.Conn.InvokeAsync("SetTyping", true);
            await WaitUntil(() => host.Typing.Any(t => t.Name == "Bravo" && t.IsTyping),
                "Bravo's typing (true) never reached the host");
            await bravo.Conn.InvokeAsync("SetTyping", false);
            await WaitUntil(() => host.Typing.Any(t => t.Name == "Bravo" && !t.IsTyping),
                "Bravo's typing (false) never reached the host");
            _out.WriteLine("Human typing broadcast to the group (ok)");

            // ---- AI fakes typing: PlayerTyping(aiName, true) arrives, before it answers ----
            await WaitUntil(() => host.Typing.Any(t => t.Name == aiName && t.IsTyping),
                "the AI never showed a typing indicator");

            // Its typing-true must land before round 1's reveal (i.e. before it "submitted").
            await WaitUntil(() => host.RevealsByRound.ContainsKey(1), "round 1 reveal missing", 15);
            var aiTypingTrueAt = host.Typing.First(t => t.Name == aiName && t.IsTyping).At;
            Assert.True(aiTypingTrueAt <= host.Reveal1At,
                "the AI's typing indicator must precede its answer landing");

            // And it clears (false) — the bubble never sticks past the answer.
            await WaitUntil(() => host.Typing.Any(t => t.Name == aiName && !t.IsTyping),
                "the AI's typing indicator never cleared");
            _out.WriteLine("AI faked a typing indicator with lead time, then cleared it (ok)");

            // ---- ANONYMITY: the AI's typing events are shape-identical to a human's ----
            var aiEvents = host.Typing.Where(t => t.Name == aiName).ToList();
            var humanEvents = host.Typing.Where(t => t.Name == "Bravo").ToList();
            Assert.NotEmpty(aiEvents);
            Assert.NotEmpty(humanEvents);
            // Both carry only (string name, bool isTyping) — no extra fields exist to leak.
            Assert.All(host.Typing, t => Assert.False(string.IsNullOrEmpty(t.Name)));
        }
        finally
        {
            foreach (var c in clients)
                await c.Conn.DisposeAsync();
        }
    }

    private static int TotalTyping(List<Player> clients) => clients.Sum(c => c.Typing.Count);

    private Lobby ResolveLobby(string code)
    {
        var store = _factory.Services.GetRequiredService<LobbyStore>();
        return store.Get(code) ?? throw new Xunit.Sdk.XunitException($"lobby {code} not in store");
    }

    private static void WireEvents(Player c)
    {
        var conn = c.Conn;
        conn.On<LobbyState>("LobbyUpdated", s => c.LastLobby = s);
        conn.On<List<RosterEntry>>("GameStarted", r => c.Roster = r);
        conn.On<string, int, DateTime>("PromptStarted", (prompt, round, _) => c.CurrentRound = round);
        conn.On<Reveal>("AnswersRevealed", r =>
        {
            c.RevealsByRound[r.Round] = r;
            if (r.Round == 1) c.Reveal1At = DateTime.UtcNow;
        });
        conn.On<string, bool>("PlayerTyping", (name, isTyping) =>
            c.Typing.Add(new TypingEvent(name, isTyping, DateTime.UtcNow)));
    }

    private async Task<string> RegisterAsync(string username, string displayName)
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register",
            new { username, displayName, password = "Password123" });
        if (!res.IsSuccessStatusCode)
            throw new Xunit.Sdk.XunitException($"register failed {(int)res.StatusCode}");
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

    private sealed class Player
    {
        public Player(string name, HubConnection conn) { Name = name; Conn = conn; }
        public string Name { get; }
        public HubConnection Conn { get; }

        public LobbyState? LastLobby;
        public List<RosterEntry>? Roster;
        public int CurrentRound;
        public readonly ConcurrentDictionary<int, Reveal> RevealsByRound = new();
        public DateTime Reveal1At = DateTime.MaxValue;
        public readonly ConcurrentBag<TypingEvent> Typing = new();
    }

    private record TypingEvent(string Name, bool IsTyping, DateTime At);
    private record AuthResponse(string Token, string DisplayName, string Username);
    private record LobbyState(string Code, string State, List<PlayerEntry> Players);
    private record PlayerEntry(string DisplayName, int TokensRemaining, bool IsConnected, bool IsHost);
    private record RosterEntry(string DisplayName, int TokensRemaining);
    private record RevealAnswer(string DisplayName, string Text);
    private record Reveal(int Round, string Prompt, List<RevealAnswer> Answers);
}
