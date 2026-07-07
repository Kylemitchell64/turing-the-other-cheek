using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using GameApi.Data;
using GameApi.Hubs;
using GameApi.Lobbies;
using GameApi.Models;

namespace GameApi.GameLoop;

// The server-side round-loop driver. A single hosted service ticks every
// GameTimings.TickMilliseconds, walks every active lobby, and advances its state
// machine when a phase deadline (all UTC) has passed. All timing is server-side —
// client clocks are never trusted. Every lobby mutation takes lobby.Sync.
//
// State flow per round:
//   Prompting -> Revealing -> Accusing -> (accusation? VetoWindow) -> next round | Ended
public class GameEngine : BackgroundService
{
    private readonly LobbyStore _store;
    private readonly IHubContext<GameHub> _hub;
    private readonly IAiBrain _brain;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GameTimings _timings;
    private readonly ILogger<GameEngine> _logger;

    public GameEngine(
        LobbyStore store,
        IHubContext<GameHub> hub,
        IAiBrain brain,
        IServiceScopeFactory scopeFactory,
        GameTimings timings,
        ILogger<GameEngine> logger)
    {
        _store = store;
        _hub = hub;
        _brain = brain;
        _scopeFactory = scopeFactory;
        _timings = timings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromMilliseconds(Math.Max(50, _timings.TickMilliseconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var lobby in _store.All)
                    await TickLobbyAsync(lobby, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GameEngine tick failed");
            }

            try { await Task.Delay(period, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    // Kick off a brand-new game (or a rematch). Called by the hub under lobby.Sync
    // already held; PromptStarted goes into outbound for the hub to send off-lock.
    public void BeginGame(Lobby lobby, List<Func<Task>> outbound)
    {
        lobby.StartedAtUtc = DateTime.UtcNow;
        StartPromptingRound(lobby, roundNumber: 1, outbound);
    }

    // ---- the tick: dispatch on current phase, act only when its deadline passed ----

    private async Task TickLobbyAsync(Lobby lobby, CancellationToken ct)
    {
        // Snapshot what (if anything) to do, then send events OUTSIDE the lock.
        List<Func<Task>> outbound = new();

        lock (lobby.Sync)
        {
            switch (lobby.State)
            {
                case GameState.Prompting:
                    MaybeRequestAiAnswer(lobby);
                    if (DateTime.UtcNow >= lobby.PhaseDeadlineUtc)
                        EnterRevealing(lobby, outbound);
                    break;

                case GameState.Revealing:
                    if (DateTime.UtcNow >= lobby.PhaseDeadlineUtc)
                        EnterAccusing(lobby, outbound);
                    break;

                case GameState.Accusing:
                    // Priority sub-window ended with no accusation → open the general window.
                    if (lobby.InPriorityWindow && DateTime.UtcNow >= lobby.PhaseDeadlineUtc
                        && lobby.AccuserName == null)
                    {
                        OpenGeneralAccusationWindow(lobby, outbound);
                    }
                    else if (!lobby.InPriorityWindow && DateTime.UtcNow >= lobby.PhaseDeadlineUtc
                        && lobby.AccuserName == null)
                    {
                        // No accusation this round → advance.
                        AdvanceAfterRound(lobby, outbound);
                    }
                    break;

                case GameState.VetoWindow:
                    if (DateTime.UtcNow >= lobby.PhaseDeadlineUtc)
                        ResolveAccusation(lobby, outbound); // nobody vetoed in time
                    break;
            }
        }

        foreach (var send in outbound)
            await send();
    }

    // ---- Prompting ----

    private void StartPromptingRound(Lobby lobby, int roundNumber, List<Func<Task>> outbound)
    {
        lobby.RoundNumber = roundNumber;
        lobby.State = GameState.Prompting;
        lobby.CurrentPrompt = PromptPool.Random();
        lobby.Answers.Clear();
        lobby.AiAnswerRequested = false;
        lobby.AccuserName = null;
        lobby.AccusedName = null;
        lobby.VetoEligible.Clear();
        lobby.InPriorityWindow = false;
        lobby.PhaseDeadlineUtc = DateTime.UtcNow + _timings.Prompt;

        var code = lobby.Code;
        var prompt = lobby.CurrentPrompt;
        var round = lobby.RoundNumber;
        var deadline = lobby.PhaseDeadlineUtc;

        outbound.Add(() => _hub.Clients.Group(code).SendAsync("PromptStarted", prompt, round, deadline));
    }

    // Fire the AI's answer task once per round, in the background. The brain returns
    // a delay; when it elapses we write the answer under the lobby lock.
    private void MaybeRequestAiAnswer(Lobby lobby)
    {
        if (lobby.AiAnswerRequested) return;
        lobby.AiAnswerRequested = true;

        var aiName = lobby.AiDisplayName!;
        var humans = lobby.Players.Select(p => p.DisplayName).ToList();
        var history = BuildHistory(lobby);
        var remaining = lobby.PhaseDeadlineUtc - DateTime.UtcNow;
        var code = lobby.Code;
        var round = lobby.RoundNumber;
        var prompt = lobby.CurrentPrompt;

        var ctx = new AiTurnContext(
            CurrentPrompt: prompt,
            RoundNumber: round,
            AiDisplayName: aiName,
            HumanDisplayNames: humans,
            History: history,
            StyleSummaries: Array.Empty<string>(),
            TimeRemaining: remaining);

        _ = Task.Run(async () =>
        {
            try
            {
                var answer = await _brain.AnswerAsync(ctx, CancellationToken.None);
                if (answer.Delay > TimeSpan.Zero)
                    await Task.Delay(answer.Delay);

                lock (lobby.Sync)
                {
                    // Only record if we're still in the same prompting round.
                    if (lobby.State == GameState.Prompting && lobby.RoundNumber == round
                        && !lobby.Answers.ContainsKey(aiName))
                    {
                        RecordAnswer(lobby, aiName, answer.Text, authorUserId: null, isAi: true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI answer task failed for lobby {Code}", code);
            }
        });
    }

    private void EnterRevealing(Lobby lobby, List<Func<Task>> outbound)
    {
        // Anyone who didn't answer (including the AI, if its delay somehow lapsed)
        // gets a blank so the reveal has an entry for every seat.
        EnsureAnswerFor(lobby, lobby.AiDisplayName!, isAi: true);
        foreach (var p in lobby.Players)
            EnsureAnswerFor(lobby, p.DisplayName, isAi: false, authorUserId: p.UserId);

        lobby.State = GameState.Revealing;
        lobby.PhaseDeadlineUtc = DateTime.UtcNow + _timings.Reveal;

        // Reveal order randomized so slot position leaks nothing about the AI.
        var revealed = lobby.Answers
            .Select(kv => new RevealedAnswerDto(kv.Key, kv.Value.Text))
            .ToList();
        Shuffle(revealed);

        var dto = new AnswersRevealedDto(lobby.RoundNumber, lobby.CurrentPrompt, revealed);
        var code = lobby.Code;
        outbound.Add(() => _hub.Clients.Group(code).SendAsync("AnswersRevealed", dto));
    }

    // ---- Accusing ----

    private void EnterAccusing(Lobby lobby, List<Func<Task>> outbound)
    {
        lobby.State = GameState.Accusing;
        lobby.AccuserName = null;
        lobby.AccusedName = null;

        var code = lobby.Code;

        // Blackout round: the one full round after a veto — NO accusation window for
        // anyone. Just wait out a short window then advance.
        if (lobby.BlackoutRound == lobby.RoundNumber)
        {
            lobby.InPriorityWindow = false;
            lobby.PhaseDeadlineUtc = DateTime.UtcNow + _timings.Reveal; // brief pause, no window opened
            // No AccusationWindowOpened event — clients show no accuse UI this round.
            return;
        }

        // Priority round: the vetoer gets an exclusive 5s window before the general one.
        if (lobby.PriorityRound == lobby.RoundNumber && lobby.PriorityVetoerName != null)
        {
            var vetoer = lobby.FindPlayerByName(lobby.PriorityVetoerName);
            if (vetoer != null && !vetoer.IsEliminated)
            {
                lobby.InPriorityWindow = true;
                lobby.PhaseDeadlineUtc = DateTime.UtcNow + _timings.Priority;
                var deadline = lobby.PhaseDeadlineUtc;
                var priorityName = lobby.PriorityVetoerName;
                outbound.Add(() => _hub.Clients.Group(code)
                    .SendAsync("AccusationWindowOpened", deadline, priorityName));
                return;
            }
        }

        // Normal general window.
        OpenGeneralAccusationWindow(lobby, outbound);
    }

    private void OpenGeneralAccusationWindow(Lobby lobby, List<Func<Task>> outbound)
    {
        lobby.InPriorityWindow = false;
        // Priority is a one-shot: consume it once its round opens the general window.
        if (lobby.PriorityRound == lobby.RoundNumber)
        {
            lobby.PriorityRound = null;
            lobby.PriorityVetoerName = null;
        }

        lobby.PhaseDeadlineUtc = DateTime.UtcNow + _timings.Accusation;
        var deadline = lobby.PhaseDeadlineUtc;
        var code = lobby.Code;
        outbound.Add(() => _hub.Clients.Group(code)
            .SendAsync("AccusationWindowOpened", deadline, (string?)null));
    }

    // ---- VetoWindow ----

    // Called by the hub (under lock) when an accusation locks in. Opens the veto
    // window to OTHER token-holders only.
    public void OpenVetoWindow(Lobby lobby, List<Func<Task>> outbound)
    {
        lobby.State = GameState.VetoWindow;
        lobby.InPriorityWindow = false;
        lobby.VetoEligible.Clear();

        // If this accusation came in during a priority window, that priority is now
        // spent — don't re-offer it in a later round.
        if (lobby.PriorityRound == lobby.RoundNumber)
        {
            lobby.PriorityRound = null;
            lobby.PriorityVetoerName = null;
        }

        var code = lobby.Code;
        var eligibleConnIds = new List<string>();
        foreach (var p in lobby.Players)
        {
            if (p.DisplayName == lobby.AccuserName) continue; // accuser can't veto their own
            if (!p.CanVeto) continue;
            lobby.VetoEligible.Add(p.DisplayName);
            eligibleConnIds.AddRange(p.ConnectionIds);
        }

        lobby.PhaseDeadlineUtc = DateTime.UtcNow + _timings.Veto;
        var deadline = lobby.PhaseDeadlineUtc;

        // AccusationMade goes to everyone; VetoWindowOpened only to eligible vetoers.
        var accuser = lobby.AccuserName!;
        var accused = lobby.AccusedName!;
        outbound.Add(() => _hub.Clients.Group(code).SendAsync("AccusationMade", accuser, accused));

        if (eligibleConnIds.Count > 0)
        {
            var ids = eligibleConnIds.ToList();
            outbound.Add(() => _hub.Clients.Clients(ids).SendAsync("VetoWindowOpened", deadline));
        }
        else
        {
            // No one can veto → resolve immediately at the deadline. Shorten the wait
            // so a game with no eligible vetoers doesn't stall for the full window.
            lobby.PhaseDeadlineUtc = DateTime.UtcNow;
        }
    }

    // Called by the hub (under lock) when someone uses a fake-out during the window.
    public void ApplyVeto(Lobby lobby, LobbyPlayer vetoer, List<Func<Task>> outbound)
    {
        vetoer.TokensRemaining--;
        vetoer.VetoerCount++;

        // Result is NEVER revealed. One full round plays with no accusation window,
        // then the vetoer gets a priority window the round after.
        lobby.BlackoutRound = lobby.RoundNumber + 1;
        lobby.PriorityRound = lobby.RoundNumber + 2;
        lobby.PriorityVetoerName = vetoer.DisplayName;

        var code = lobby.Code;
        var vetoerName = vetoer.DisplayName;
        // FakeOutUsed fires to ALL — triggers the screen shake everywhere.
        outbound.Add(() => _hub.Clients.Group(code).SendAsync("FakeOutUsed", vetoerName));

        // Advance to the next round (blackout). No AccusationResolved is ever sent.
        AdvanceAfterRound(lobby, outbound);
    }

    // ---- resolution ----

    private void ResolveAccusation(Lobby lobby, List<Func<Task>> outbound)
    {
        var accuserName = lobby.AccuserName;
        var accusedName = lobby.AccusedName;

        // Defensive: no accusation actually locked (shouldn't hit VetoWindow then).
        if (accuserName == null || accusedName == null)
        {
            AdvanceAfterRound(lobby, outbound);
            return;
        }

        var correct = string.Equals(accusedName, lobby.AiDisplayName, StringComparison.Ordinal);
        var code = lobby.Code;

        outbound.Add(() => _hub.Clients.Group(code)
            .SendAsync("AccusationResolved", correct, accuserName, accusedName));

        if (correct)
        {
            var accuser = lobby.FindPlayerByName(accuserName);
            lobby.WinType = WinType.Detector;
            lobby.WinnerUserId = accuser?.UserId;
            lobby.WinnerName = accuserName;
            EndGame(lobby, outbound);
            return;
        }

        // Wrong accusation: accuser burns a token. At 0 tokens they're
        // accusation-eliminated (answer-only, can't accuse or veto).
        var loser = lobby.FindPlayerByName(accuserName);
        if (loser != null)
        {
            loser.TokensRemaining = Math.Max(0, loser.TokensRemaining - 1);
            if (loser.TokensRemaining == 0 && !loser.IsEliminated)
            {
                loser.IsEliminated = true;
                var name = loser.DisplayName;
                outbound.Add(() => _hub.Clients.Group(code).SendAsync("PlayerEliminated", name));
            }
        }

        // All humans accusation-eliminated → AI survives.
        if (lobby.Players.All(p => p.IsEliminated))
        {
            lobby.WinType = WinType.AiSurvival;
            EndGame(lobby, outbound);
            return;
        }

        AdvanceAfterRound(lobby, outbound);
    }

    // Move to the next round, or end on the round cap.
    private void AdvanceAfterRound(Lobby lobby, List<Func<Task>> outbound)
    {
        if (lobby.RoundNumber >= _timings.MaxRounds)
        {
            // 8 rounds passed with no successful accusation → AI survival win.
            lobby.WinType = WinType.AiSurvival;
            EndGame(lobby, outbound);
            return;
        }

        StartPromptingRound(lobby, lobby.RoundNumber + 1, outbound);
    }

    private void EndGame(Lobby lobby, List<Func<Task>> outbound)
    {
        lobby.State = GameState.Ended;

        var transcript = lobby.Transcript
            .Select(r => new TranscriptMessageDto(r.Round, r.DisplayName, r.Text, r.IsAi))
            .ToList();

        var dto = new GameEndedDto(
            WinType: lobby.WinType.ToString(),
            WinnerName: lobby.WinnerName,
            AiRealIdentityName: lobby.AiDisplayName!,
            FullTranscript: transcript);

        var code = lobby.Code;
        outbound.Add(() => _hub.Clients.Group(code).SendAsync("GameEnded", dto));

        // Persist to the DB (real author ids) off the lock, on the thread pool.
        var snapshot = SnapshotForPersist(lobby);
        outbound.Add(() => PersistAsync(snapshot));
    }

    // ---- helpers ----

    private static void EnsureAnswerFor(Lobby lobby, string name, bool isAi, string? authorUserId = null)
    {
        if (!lobby.Answers.ContainsKey(name))
            RecordAnswer(lobby, name, "(no answer)", authorUserId, isAi);
    }

    // Record an answer both in the live dictionary and the permanent transcript.
    private static void RecordAnswer(Lobby lobby, string name, string text, string? authorUserId, bool isAi)
    {
        lobby.Answers[name] = new RoundAnswer { Text = text, IsAi = isAi };
        lobby.Transcript.Add(new RecordedAnswer
        {
            Round = lobby.RoundNumber,
            DisplayName = name,
            AuthorUserId = authorUserId,
            Text = text,
            IsAi = isAi,
            SentAtUtc = DateTime.UtcNow
        });
    }

    private static List<RoundHistory> BuildHistory(Lobby lobby)
    {
        return lobby.Transcript
            .GroupBy(r => r.Round)
            .OrderBy(g => g.Key)
            .Select(g => new RoundHistory(
                g.Key,
                lobby.CurrentPrompt, // best-effort; per-round prompt text isn't stored separately in v1
                g.Select(r => new HistoryAnswer(r.DisplayName, r.Text)).ToList()))
            .ToList();
    }

    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // ---- persistence (game end only) ----

    private record PersistSnapshot(
        string JoinCode,
        WinType WinType,
        string? WinnerUserId,
        DateTime StartedAt,
        List<(string UserId, int Tokens, bool Eliminated, int VetoerCount)> Players,
        List<(int Round, string? AuthorUserId, string DisplayName, string Text, DateTime SentAt)> Messages);

    private static PersistSnapshot SnapshotForPersist(Lobby lobby) => new(
        JoinCode: lobby.Code,
        WinType: lobby.WinType,
        WinnerUserId: lobby.WinnerUserId,
        StartedAt: lobby.StartedAtUtc,
        Players: lobby.Players
            .Select(p => (p.UserId, p.TokensRemaining, p.IsEliminated, p.VetoerCount))
            .ToList(),
        Messages: lobby.Transcript
            .Select(r => (r.Round, r.AuthorUserId, r.DisplayName, r.Text, r.SentAtUtc))
            .ToList());

    // Write Games / GamePlayers / GameMessages at game end. Real author ids land
    // in the DB (null == AI); they never went over the wire live.
    private async Task PersistAsync(PersistSnapshot snap)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();

            var game = new Models.Game
            {
                JoinCode = snap.JoinCode,
                State = GameState.Ended,
                WinType = snap.WinType,
                WinnerUserId = snap.WinnerUserId,
                StartedAt = snap.StartedAt,
                EndedAt = DateTime.UtcNow
            };

            foreach (var p in snap.Players)
            {
                game.Players.Add(new GamePlayer
                {
                    UserId = p.UserId,
                    TokensRemaining = p.Tokens,
                    IsEliminated = p.Eliminated,
                    VetoerCount = p.VetoerCount
                });
            }

            foreach (var m in snap.Messages)
            {
                game.Messages.Add(new GameMessage
                {
                    Round = m.Round,
                    AuthorUserId = m.AuthorUserId, // null == AI
                    AuthorDisplayNameAtTime = m.DisplayName,
                    Text = m.Text,
                    SentAt = m.SentAt
                });
            }

            db.Games.Add(game);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Persistence failing must never crash the engine loop. Log and move on;
            // the game already ended for the players.
            _logger.LogError(ex, "Persisting game {Code} failed", snap.JoinCode);
        }
    }
}
