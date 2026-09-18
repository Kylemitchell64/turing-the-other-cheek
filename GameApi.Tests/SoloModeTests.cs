using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace GameApi.Tests;

// Phase 29: solo demo mode + rejoin. One human starts a game alone; three bot stand-ins
// are seated with the AI, they all answer every round, the game runs its 5-round cap and
// ends as a solo game. Plus: a second connection for the same user can Rejoin mid-game and
// gets a snapshot that matches the live state.
public class SoloModeTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public SoloModeTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task SoloStart_SeatsThreeBotsPlusAi_AndEveryoneAnswers()
    {
        var token = await RegisterAsync("solo_" + Guid.NewGuid().ToString("N")[..8], "Loner");
        await using var conn = BuildConnection(token);

        LobbyState? lobby = null;
        List<RosterEntry>? roster = null;
        Reveal? firstReveal = null;
        conn.On<LobbyState>("LobbyUpdated", s => lobby = s);
        conn.On<List<RosterEntry>>("GameStarted", r => roster = r);
        conn.On<Reveal>("AnswersRevealed", r => firstReveal ??= r);

        await conn.StartAsync();
        await conn.InvokeAsync("CreateLobby");
        await WaitFor(() => lobby != null, "no LobbyUpdated", 5);

        // A normal start with one player is still refused; the solo start is not.
        var ex = await Assert.ThrowsAsync<HubException>(() => conn.InvokeAsync("StartGame"));
        Assert.Contains("at least 3", ex.Message);

        await conn.InvokeAsync("StartSoloGame");
        await WaitFor(() => roster != null, "no GameStarted", 5);

        // 1 human + 3 bots + the AI, every seat with 3 tokens, none flagged in any way.
        Assert.Equal(5, roster!.Count);
        Assert.Contains(roster, r => r.DisplayName == "Loner");
        Assert.All(roster, r => Assert.Equal(3, r.TokensRemaining));
        Assert.Equal(5, roster.Select(r => r.DisplayName).Distinct().Count());

        // Bots show as connected seats in the lobby roster (no "off" dots on a demo).
        await WaitFor(() => lobby!.Players.Count == 4, "bots never seated");
        Assert.All(lobby!.Players, p => Assert.True(p.IsConnected));

        // Every seat answers round 1 (bots on the shared typing/submit path, the AI via the
        // mock brain, and the human gets a "(no answer)" blank since we never typed).
        await WaitFor(() => firstReveal != null, "round 1 never revealed", 20);
        Assert.Equal(5, firstReveal!.Answers.Count);
        var blanks = firstReveal.Answers.Count(a => a.Text == "(no answer)");
        Assert.Equal(1, blanks); // only the idle human
    }

    [Fact]
    public async Task SoloGame_EndsAfterFiveRounds_FlaggedSolo()
    {
        var token = await RegisterAsync("solo_" + Guid.NewGuid().ToString("N")[..8], "Loner2");
        await using var conn = BuildConnection(token);

        LobbyState? lobby = null;
        GameEnded? ended = null;
        var rounds = new HashSet<int>();
        var fakeOuts = new List<string>();
        var accusations = new List<(string, string)>();
        var tokenChanges = new List<(string, int, string)>();
        conn.On<string, int, string>("TokensChanged", (n, t, r) => tokenChanges.Add((n, t, r)));
        conn.On<LobbyState>("LobbyUpdated", s => lobby = s);
        conn.On<string, int, DateTime>("PromptStarted", (_, n, _) => rounds.Add(n));
        conn.On<string, string>("AccusationMade", (a, b) => accusations.Add((a, b)));
        conn.On<string>("FakeOutUsed", v => fakeOuts.Add(v));
        conn.On<GameEnded>("GameEnded", g => ended = g);

        await conn.StartAsync();
        await conn.InvokeAsync("CreateLobby");
        await WaitFor(() => lobby != null, "no LobbyUpdated", 5);
        await conn.InvokeAsync("StartSoloGame");

        await WaitFor(() => ended != null, "solo game never ended", 60);
        Assert.True(ended!.IsSolo);
        Assert.Equal("AiSurvival", ended.WinType);
        Assert.Equal(5, rounds.Count); // solo cap, not the classic 8
        Assert.Equal(5, ended.Prompts.Count);

        // The scripted drama (forced on in tests): a bot accused someone and a DIFFERENT bot
        // faked-out, so every accusation is "vetoed" — nothing was ever revealed — and the
        // client saw both the accusation and the shake. Never the human as accuser/vetoer.
        Assert.NotEmpty(ended.Accusations);
        Assert.All(ended.Accusations, a =>
        {
            Assert.Equal("vetoed", a.Outcome);
            Assert.NotNull(a.Vetoer);
            Assert.NotEqual(a.Accuser, a.Vetoer);
            Assert.NotEqual("Loner2", a.Accuser);
            Assert.NotEqual("Loner2", a.Vetoer);
        });
        Assert.True(ended.Accusations.Count <= 2);
        Assert.Equal(ended.Accusations.Count, accusations.Count);
        Assert.Equal(ended.Accusations.Count, fakeOuts.Count);
        // the idle human blanked all 5 rounds: half a token each → 2 tokens gone (after r2, r4)
        Assert.Equal(2, tokenChanges.Count);
        Assert.All(tokenChanges, c => { Assert.Equal("Loner2", c.Item1); Assert.Equal("no answer", c.Item3); });
        Assert.Equal(new[] { 2, 1 }, tokenChanges.Select(c => c.Item2).ToArray());

        // 5 rounds x 5 seats of transcript, the AI's lines flagged only here
        Assert.Equal(25, ended.FullTranscript.Count);
        Assert.Equal(5, ended.FullTranscript.Count(m => m.IsAi));
    }

    [Fact]
    public async Task Rejoin_FromANewConnection_ReturnsLiveSnapshot()
    {
        var token = await RegisterAsync("rj_" + Guid.NewGuid().ToString("N")[..8], "Flaky");
        await using var first = BuildConnection(token);

        LobbyState? lobby = null;
        List<RosterEntry>? roster = null;
        first.On<LobbyState>("LobbyUpdated", s => lobby = s);
        first.On<List<RosterEntry>>("GameStarted", r => roster = r);
        await first.StartAsync();
        await first.InvokeAsync("CreateLobby");
        await WaitFor(() => lobby != null, "no LobbyUpdated", 5);
        var code = lobby!.Code;
        await first.InvokeAsync("StartSoloGame");
        await WaitFor(() => roster != null, "no GameStarted", 5);

        // The phone locked: the old socket is gone, a brand-new one comes up for the same user.
        await first.StopAsync();
        await using var second = BuildConnection(token);
        LobbyState? seenOnSecond = null;
        second.On<LobbyState>("LobbyUpdated", s => seenOnSecond = s);
        await second.StartAsync();

        var snap = await second.InvokeAsync<Resync?>("Rejoin");
        Assert.NotNull(snap);
        Assert.Equal(code, snap!.Lobby.Code);
        Assert.NotNull(snap.Roster);
        Assert.Equal(5, snap.Roster!.Count);
        Assert.True(snap.Round >= 1);
        Assert.False(string.IsNullOrEmpty(snap.Prompt));
        Assert.Contains(snap.Phase, new[] { "Prompting", "Revealing", "Accusing", "VetoWindow", "Ended" });
        Assert.Equal(5, snap.Tokens.Count);
        Assert.Null(snap.Ended);

        // And it's back in the group: the broadcast that Rejoin fires reaches the new socket.
        await WaitFor(() => seenOnSecond != null, "rejoined socket never got LobbyUpdated", 5);

        // A user with no seat gets null, not an error.
        var strayToken = await RegisterAsync("stray_" + Guid.NewGuid().ToString("N")[..8], "Stray");
        await using var stray = BuildConnection(strayToken);
        await stray.StartAsync();
        Assert.Null(await stray.InvokeAsync<Resync?>("Rejoin"));
    }

    // --- helpers ---

    private async Task<string> RegisterAsync(string username, string displayName)
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new { username, displayName, password = "Password123" });
        res.EnsureSuccessStatusCode();
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

    private static async Task WaitFor(Func<bool> condition, string message, int seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
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
    private record RosterEntry(string DisplayName, int TokensRemaining);
    private record RevealedAnswer(string DisplayName, string Text);
    private record Reveal(int Round, string Prompt, List<RevealedAnswer> Answers);
    private record TranscriptLine(int Round, string DisplayName, string Text, bool IsAi);
    private record AccusationLine(int Round, string Accuser, string Accused, string Outcome, string? Vetoer);
    private record RoundPrompt(int Round, string Prompt);
    private record GameEnded(string WinType, string? WinnerName, string AiRealIdentityName,
        List<TranscriptLine> FullTranscript, List<AccusationLine> Accusations, List<RoundPrompt> Prompts, bool IsSolo);
    private record Resync(LobbyState Lobby, List<RosterEntry>? Roster, string Phase, int Round, string Prompt,
        DateTime DeadlineUtc, bool AnsweredThisRound, Dictionary<string, int> Tokens, GameEnded? Ended);
}
