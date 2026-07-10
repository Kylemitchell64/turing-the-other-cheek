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
    private readonly IAiGuesser _guesser;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StyleSummarizer _summarizer;
    private readonly GroupProfiler _groupProfiler;
    private readonly GameTimings _timings;
    private readonly ILogger<GameEngine> _logger;

    // Reverse mode (phase 22) runs a fixed 6 rounds regardless of the classic MaxRounds cap.
    private const int ReverseRounds = 6;

    // Upper bound the reverse reveal waits on the attribution task before advancing anyway.
    // The guesser always returns fast (it random-falls-back internally), so this is only a
    // safety net against a truly stuck task — never the normal path.
    private static readonly TimeSpan ReverseAnalysisCap = TimeSpan.FromSeconds(15);

    public GameEngine(
        LobbyStore store,
        IHubContext<GameHub> hub,
        IAiBrain brain,
        IAiGuesser guesser,
        IServiceScopeFactory scopeFactory,
        StyleSummarizer summarizer,
        GroupProfiler groupProfiler,
        GameTimings timings,
        ILogger<GameEngine> logger)
    {
        _store = store;
        _hub = hub;
        _brain = brain;
        _guesser = guesser;
        _scopeFactory = scopeFactory;
        _summarizer = summarizer;
        _groupProfiler = groupProfiler;
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
        LoadStyleSummaries(lobby);
        LoadGroupNotes(lobby);
        StartPromptingRound(lobby, roundNumber: 1, outbound);
    }

    // Crew game (phase 19): load the crew's current GroupProfileJson and render it into the
    // GROUP NOTES block the impostor plays with. NORMAL and HARD get it; EASY never does.
    // Best-effort — any failure just means the AI plays without the group notes this game.
    private void LoadGroupNotes(Lobby lobby)
    {
        lobby.GroupNotes = null;
        if (lobby.CrewId == null) return;
        if (DifficultyProfile.Get(lobby.Difficulty).EasyPersona) return; // EASY never gets them

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();
            var json = db.Crews
                .Where(c => c.Id == lobby.CrewId)
                .Select(c => c.GroupProfileJson)
                .FirstOrDefault();
            lobby.GroupNotes = GroupProfiler.RenderNotes(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading group notes failed for lobby {Code}", lobby.Code);
            lobby.GroupNotes = null;
        }
    }

    // Load each human's StyleProfiles.SummaryJson (if any) into the lobby as
    // "NAME: {json}" lines for the AI system prompt. Omitted entirely if none exist.
    // Best-effort: a DB failure just means the AI plays without style notes.
    private void LoadStyleSummaries(Lobby lobby)
    {
        lobby.StyleSummaries.Clear();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();

            var userIds = lobby.Players.Select(p => p.UserId).ToList();
            var profiles = db.StyleProfiles
                .Where(sp => userIds.Contains(sp.UserId) && sp.SummaryJson != null)
                .ToDictionary(sp => sp.UserId, sp => sp.SummaryJson!);

            foreach (var p in lobby.Players)
                if (profiles.TryGetValue(p.UserId, out var json))
                    lobby.StyleSummaries.Add($"{p.DisplayName}: {json}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading style summaries failed for lobby {Code}", lobby.Code);
            lobby.StyleSummaries.Clear();
        }
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
                    // Reverse mode has no impostor — nobody generates an AI answer; every
                    // seat is a real human. Classic fires the impostor's answer task here.
                    if (!GameModes.IsReverse(lobby.Mode))
                        MaybeRequestAiAnswer(lobby);
                    if (DateTime.UtcNow >= lobby.PhaseDeadlineUtc)
                        EnterRevealing(lobby, outbound);
                    break;

                case GameState.Revealing:
                    if (GameModes.IsReverse(lobby.Mode))
                    {
                        // The reveal window doubles as the AI's "analyzing" window: fire the
                        // attribution task once, and advance when the read window (reset by
                        // the task once its guesses land) or the safety cap expires. No
                        // accuse/veto phases in reverse.
                        MaybeRequestReverseGuess(lobby);
                        if (DateTime.UtcNow >= lobby.PhaseDeadlineUtc)
                            AdvanceAfterRound(lobby, outbound);
                    }
                    else if (DateTime.UtcNow >= lobby.PhaseDeadlineUtc)
                    {
                        EnterAccusing(lobby, outbound);
                    }
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
        lobby.CurrentPrompt = lobby.PackKey == PromptPacks.CustomKey && lobby.CustomPack != null
            ? PromptPacks.PickPrompt(lobby.CustomPack.Prompts, lobby.UsedPromptIndices)
            : PromptPacks.PickPrompt(lobby.PackKey, lobby.UsedPromptIndices);
        lobby.RoundPrompts[roundNumber] = lobby.CurrentPrompt;
        lobby.Answers.Clear();
        lobby.AiAnswerRequested = false;
        lobby.AccuserName = null;
        lobby.AccusedName = null;
        lobby.VetoEligible.Clear();
        lobby.InPriorityWindow = false;
        lobby.PhaseDeadlineUtc = DateTime.UtcNow + PromptWindow(lobby);

        var code = lobby.Code;
        var prompt = lobby.CurrentPrompt;
        var round = lobby.RoundNumber;
        var deadline = lobby.PhaseDeadlineUtc;

        outbound.Add(() => _hub.Clients.Group(code).SendAsync("PromptStarted", prompt, round, deadline));
    }

    // The answer window for this lobby: the host's pace pick, except "standard" which
    // defers to configured GameTimings so the compressed test timings keep working.
    private TimeSpan PromptWindow(Lobby lobby) =>
        lobby.PaceKey == PaceOptions.DefaultKey
            ? _timings.Prompt
            : TimeSpan.FromSeconds(PaceOptions.WindowSeconds(lobby.PaceKey));

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

        // Group stats over every real answer so far (post-processing + timing median).
        var priorAnswers = lobby.Transcript.Select(r => r.Text).ToList();
        var groupStats = AnswerPostProcessor.ComputeStats(priorAnswers, StyleTypoRates(lobby));

        // The AI's own prior answers, for personality continuity.
        var ownAnswers = lobby.Transcript
            .Where(r => r.IsAi)
            .OrderBy(r => r.Round)
            .Select(r => r.Text)
            .ToList();

        var ctx = new AiTurnContext(
            CurrentPrompt: prompt,
            RoundNumber: round,
            AiDisplayName: aiName,
            HumanDisplayNames: humans,
            History: history,
            PreviousOwnAnswers: ownAnswers,
            StyleSummaries: BuildStyleSummaries(lobby),
            GroupStats: groupStats,
            TimingState: lobby.TimingState,
            FallbackState: lobby.FallbackState,
            TimeRemaining: remaining,
            PackKey: lobby.PackKey,
            Difficulty: lobby.Difficulty,
            WindowSeconds: PromptWindow(lobby).TotalSeconds,
            CustomNsfw: lobby.CustomPack?.Nsfw ?? false,
            GroupNotes: lobby.GroupNotes);

        _ = Task.Run(async () =>
        {
            try
            {
                var answer = await _brain.AnswerAsync(ctx, CancellationToken.None);
                await RunAiTypingAndSubmit(lobby, aiName, round, answer.Text, answer.Delay);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI answer task failed for lobby {Code}", code);
            }
        });
    }

    // Drive the AI's fake typing indicator, then submit at the scheduled time. The AI
    // "starts typing" TypingDuration before its submit instant and "stops" the moment
    // the answer lands — exactly like a human whose indicator reflects what they sent.
    // Everything is anchored to absolute UTC instants so a hesitation blip can't drift
    // the submit time. All broadcasts are the plain PlayerTyping(name, bool) — the same
    // payload humans produce, so nothing here singles the AI out.
    private async Task RunAiTypingAndSubmit(Lobby lobby, string aiName, int round, string text, TimeSpan delay)
    {
        var rng = Random.Shared;
        var now = DateTime.UtcNow;
        var submitAt = now + (delay > TimeSpan.Zero ? delay : TimeSpan.Zero);

        var typingLead = AnswerTiming.TypingDuration(text.Length, rng);
        var typingStart = submitAt - typingLead;
        if (typingStart < now) typingStart = now; // short window: start typing right away

        // Optional single hesitation blip: one brief false-start in the dead time before
        // real typing begins, but only when there's comfortable lead so it reads natural.
        var leadSecs = (typingStart - now).TotalSeconds;
        if (leadSecs > 4.0 && rng.NextDouble() < 0.35)
        {
            var blipAt = now.AddSeconds(rng.NextDouble() * (leadSecs - 2.5));
            await SleepUntil(blipAt);
            if (!await SetAiTyping(lobby, aiName, round, true)) return;
            await Task.Delay(TimeSpan.FromSeconds(0.6 + rng.NextDouble() * 0.6));
            if (!await SetAiTyping(lobby, aiName, round, false)) return;
        }

        await SleepUntil(typingStart);
        if (!await SetAiTyping(lobby, aiName, round, true)) return;

        await SleepUntil(submitAt);

        var recorded = false;
        lock (lobby.Sync)
        {
            // Only record if we're still in the same prompting round.
            if (lobby.State == GameState.Prompting && lobby.RoundNumber == round
                && !lobby.Answers.ContainsKey(aiName))
            {
                RecordAnswer(lobby, aiName, text, authorUserId: null, isAi: true);
                recorded = true;
            }
            lobby.TypingNames.Remove(aiName);
        }

        // Typing stops when the answer is in — same as a human submit. (If the round had
        // already moved on, EnterRevealing's clear already fired; a redundant false is
        // harmless.)
        if (recorded)
            await _hub.Clients.Group(lobby.Code).SendAsync("PlayerTyping", aiName, false);
    }

    // Set the AI's typing flag (guarded to the current prompting round) and broadcast it.
    // Returns false — telling the caller to abort — if the round has moved on.
    private async Task<bool> SetAiTyping(Lobby lobby, string aiName, int round, bool typing)
    {
        bool live;
        lock (lobby.Sync)
        {
            live = lobby.State == GameState.Prompting && lobby.RoundNumber == round
                && !lobby.Answers.ContainsKey(aiName);
            if (!live) lobby.TypingNames.Remove(aiName);
            else if (typing) lobby.TypingNames.Add(aiName);
            else lobby.TypingNames.Remove(aiName);
        }
        if (!live) return false;
        await _hub.Clients.Group(lobby.Code).SendAsync("PlayerTyping", aiName, typing);
        return true;
    }

    private static async Task SleepUntil(DateTime utc)
    {
        var wait = utc - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
    }

    private void EnterRevealing(Lobby lobby, List<Func<Task>> outbound)
    {
        if (GameModes.IsReverse(lobby.Mode))
        {
            EnterReverseRevealing(lobby, outbound);
            return;
        }

        // Prompting is over — clear every lingering typing indicator (humans still in
        // the box, or the AI if its task hasn't cleared itself yet) so no bubble sticks.
        if (lobby.TypingNames.Count > 0)
        {
            var code0 = lobby.Code;
            foreach (var typer in lobby.TypingNames.ToList())
                outbound.Add(() => _hub.Clients.Group(code0).SendAsync("PlayerTyping", typer, false));
            lobby.TypingNames.Clear();
        }

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

    // ---- Reverse reveal (phase 22) ----

    // No impostor to hide, so instead of the classic anonymized-but-attributed reveal we
    // shuffle everyone's answers, strip the names to anon ids (a/b/c...), and show them while
    // the AI "analyzes". The attribution task fires on the next tick and lands the guesses.
    private void EnterReverseRevealing(Lobby lobby, List<Func<Task>> outbound)
    {
        // Clear any lingering typing bubbles just like the classic reveal.
        if (lobby.TypingNames.Count > 0)
        {
            var code0 = lobby.Code;
            foreach (var typer in lobby.TypingNames.ToList())
                outbound.Add(() => _hub.Clients.Group(code0).SendAsync("PlayerTyping", typer, false));
            lobby.TypingNames.Clear();
        }

        // Every human seat gets an entry (a blank for a no-show) — there's no AI seat.
        foreach (var p in lobby.Players)
            EnsureAnswerFor(lobby, p.DisplayName, isAi: false, authorUserId: p.UserId);

        // Shuffle, then hand out stable anon ids. ReverseSlots keeps the true author (never
        // sent until the guesses are revealed) so we can score the AI.
        var entries = lobby.Answers
            .Select(kv => (Name: kv.Key, Text: kv.Value.Text))
            .ToList();
        Shuffle(entries);

        lobby.ReverseSlots.Clear();
        var anon = new List<AnonAnswerDto>();
        for (var i = 0; i < entries.Count; i++)
        {
            var id = AnonId(i);
            var author = lobby.FindPlayerByName(entries[i].Name);
            lobby.ReverseSlots[id] = new ReverseSlot
            {
                AuthorName = entries[i].Name,
                AuthorUserId = author?.UserId,
                Text = entries[i].Text
            };
            anon.Add(new AnonAnswerDto(id, entries[i].Text));
        }

        lobby.State = GameState.Revealing;
        lobby.ReverseGuessRequested = false;
        // Hold the reveal open until the guesses land (ApplyReverseGuesses resets this to a
        // short read window); the cap is only a safety net against a stuck task.
        lobby.PhaseDeadlineUtc = DateTime.UtcNow + ReverseAnalysisCap;

        var dto = new ReverseRevealStartedDto(lobby.RoundNumber, lobby.CurrentPrompt, anon);
        var code = lobby.Code;
        outbound.Add(() => _hub.Clients.Group(code).SendAsync("ReverseRevealStarted", dto));
    }

    // Anon ids: a, b, c, ... (max 8 seats, so a-h).
    private static string AnonId(int index) => ((char)('a' + index)).ToString();

    // Fire the AI's attribution task once per reveal, in the background — mirrors
    // MaybeRequestAiAnswer. The guesser never throws to us (it random-falls-back inside), so
    // ApplyReverseGuesses always runs and the round always advances.
    private void MaybeRequestReverseGuess(Lobby lobby)
    {
        if (lobby.ReverseGuessRequested) return;
        lobby.ReverseGuessRequested = true;

        var round = lobby.RoundNumber;
        var code = lobby.Code;
        var anon = lobby.ReverseSlots
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new AnonAnswer(kv.Key, kv.Value.Text))
            .ToList();
        var playerNames = lobby.Players.Select(p => p.DisplayName).ToList();
        var styleSummaries = lobby.StyleSummaries.ToList();
        var priorHistory = lobby.ReverseHistory.ToList();

        var ctx = new AiGuessContext(
            lobby.CurrentPrompt, round, anon, playerNames, styleSummaries, priorHistory);

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _guesser.GuessAsync(ctx, CancellationToken.None);
                await ApplyReverseGuesses(lobby, round, result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reverse guess task failed for lobby {Code}", code);
            }
        });
    }

    // Score the AI's guesses, bump per-player TimesReadByAi, extend the running tally + the
    // cross-round history, then broadcast AiGuessesRevealed and open the short read window.
    private async Task ApplyReverseGuesses(Lobby lobby, int round, AiGuessResult result)
    {
        AiGuessesRevealedDto? dto = null;
        lock (lobby.Sync)
        {
            // Round moved on (game ended, reset, etc.) → drop it.
            if (lobby.State != GameState.Revealing || lobby.RoundNumber != round)
                return;

            var revealed = new List<AiGuessDto>();
            var roundCorrect = 0;
            foreach (var g in result.Guesses)
            {
                if (!lobby.ReverseSlots.TryGetValue(g.AnswerId, out var slot)) continue;

                var correct = string.Equals(g.GuessedName, slot.AuthorName, StringComparison.OrdinalIgnoreCase);
                if (correct)
                {
                    roundCorrect++;
                    var author = lobby.FindPlayerByName(slot.AuthorName);
                    if (author != null) author.TimesReadByAi++;
                }

                lobby.ReverseHistory.Add(new PriorAttribution(
                    round, slot.Text, g.GuessedName, slot.AuthorName, correct));
                revealed.Add(new AiGuessDto(g.AnswerId, g.GuessedName, correct, slot.AuthorName, g.Taunt));
            }

            var roundTotal = revealed.Count;
            lobby.ReverseCorrect += roundCorrect;
            lobby.ReverseTotal += roundTotal;

            // Guesses are in — give players a fixed window to read them, then the tick advances.
            lobby.PhaseDeadlineUtc = DateTime.UtcNow + _timings.Reveal;

            dto = new AiGuessesRevealedDto(
                round, revealed, roundCorrect, roundTotal, lobby.ReverseCorrect, lobby.ReverseTotal);
        }

        if (dto != null)
            await _hub.Clients.Group(lobby.Code).SendAsync("AiGuessesRevealed", dto);
    }

    // Reverse-mode outcome: the AI wins (AiGuesser) when it correctly attributed at least
    // half of every answer across the game; otherwise the humans stayed hidden
    // (HumansHidden). Integer half via *2 so a 3/6 lands exactly on the AI's side.
    public static bool ReverseAiWon(int correct, int total) => total > 0 && correct * 2 >= total;

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
            // Track for TimesFooled: a wrong accusation counts if the AI ends up surviving.
            lobby.WrongAccuserUserIds.Add(loser.UserId);
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
        var cap = GameModes.IsReverse(lobby.Mode) ? ReverseRounds : _timings.MaxRounds;
        if (lobby.RoundNumber >= cap)
        {
            if (GameModes.IsReverse(lobby.Mode))
                // 6 reverse rounds done: the AI wins if it read the group at least half the
                // time, otherwise the humans stayed hidden.
                lobby.WinType = ReverseAiWon(lobby.ReverseCorrect, lobby.ReverseTotal)
                    ? WinType.AiGuesser
                    : WinType.HumansHidden;
            else
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
            // Reverse mode never seats a hidden AI, so there's no fake identity to unmask;
            // "the AI" is a safe stand-in the client doesn't lean on for reverse endings.
            AiRealIdentityName: lobby.AiDisplayName ?? "the AI",
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
                lobby.RoundPrompts.TryGetValue(g.Key, out var p) ? p : lobby.CurrentPrompt,
                g.Select(r => new HistoryAnswer(r.DisplayName, r.Text)).ToList()))
            .ToList();
    }

    // The cached per-human style-summary lines loaded at game start.
    private static IReadOnlyList<string> BuildStyleSummaries(Lobby lobby) => lobby.StyleSummaries;

    // On-demand style-profile refresh (missing/stale → regenerate). Called by the hub
    // at lobby start before summaries are loaded. Best-effort; the summarizer swallows
    // its own failures so a start never blocks on Gemini.
    public Task RefreshStyleProfilesAsync(IEnumerable<string> userIds) =>
        _summarizer.RefreshStaleProfilesAsync(userIds);

    // Pull each style profile's typoRate out of its JSON for the group typo mean.
    // Cheap parse; a missing/unparseable field just contributes nothing.
    private static List<double> StyleTypoRates(Lobby lobby)
    {
        var rates = new List<double>();
        foreach (var line in lobby.StyleSummaries)
        {
            var idx = line.IndexOf('{');
            if (idx < 0) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line[idx..]);
                if (doc.RootElement.TryGetProperty("typoRate", out var tr)
                    && tr.TryGetDouble(out var v))
                    rates.Add(v);
            }
            catch { /* ignore malformed profile json */ }
        }
        return rates;
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
        int? CrewId,
        WinType WinType,
        string? WinnerUserId,
        DateTime StartedAt,
        List<(string UserId, int Tokens, bool Eliminated, int VetoerCount, int TimesReadByAi)> Players,
        List<(int Round, string? AuthorUserId, string DisplayName, string Text, DateTime SentAt)> Messages,
        List<(int Round, string Prompt)> RoundPrompts,
        HashSet<string> WrongAccuserUserIds,
        int FallbackCount);

    private static PersistSnapshot SnapshotForPersist(Lobby lobby) => new(
        JoinCode: lobby.Code,
        CrewId: lobby.CrewId,
        WinType: lobby.WinType,
        WinnerUserId: lobby.WinnerUserId,
        StartedAt: lobby.StartedAtUtc,
        Players: lobby.Players
            .Select(p => (p.UserId, p.TokensRemaining, p.IsEliminated, p.VetoerCount, p.TimesReadByAi))
            .ToList(),
        Messages: lobby.Transcript
            .Select(r => (r.Round, r.AuthorUserId, r.DisplayName, r.Text, r.SentAtUtc))
            .ToList(),
        RoundPrompts: lobby.RoundPrompts
            .OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value))
            .ToList(),
        WrongAccuserUserIds: new HashSet<string>(lobby.WrongAccuserUserIds, StringComparer.Ordinal),
        FallbackCount: lobby.FallbackState.Count);

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

            foreach (var rp in snap.RoundPrompts)
            {
                game.RoundPrompts.Add(new GameRoundPrompt
                {
                    Round = rp.Round,
                    Prompt = rp.Prompt
                });
            }

            db.Games.Add(game);

            // ---- post-game harvesting: each human's answers this game feed their
            // sample pool (Source=Game). GameMessages above are the source of truth,
            // so we read the human, non-blank messages straight off the snapshot.
            var harvestedUserIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in snap.Messages)
            {
                if (m.AuthorUserId == null) continue;        // AI answer, never harvested
                if (m.Text == "(no answer)") continue;       // skipped a round, nothing to learn
                db.WritingSamples.Add(new WritingSample
                {
                    UserId = m.AuthorUserId,
                    Text = m.Text,
                    Source = SampleSource.Game,
                    CreatedAt = m.SentAt
                });
                harvestedUserIds.Add(m.AuthorUserId);
            }

            // ---- stats: every finisher +GamesPlayed; the detector +DetectorWins; on an
            // AI-survival game every wrong-accuser +TimesFooled and every finisher
            // +AiSurvivalGamesWitnessed. Rows created lazily.
            var finisherIds = snap.Players.Select(p => p.UserId).Distinct().ToList();

            // Playing a game counts as activity — bump LastSeen for every finisher so a
            // guest who's still playing is never swept by the retention job.
            var finisherUsers = await db.Users
                .Where(u => finisherIds.Contains(u.Id))
                .ToListAsync();
            foreach (var u in finisherUsers)
                u.LastSeenUtc = DateTime.UtcNow;

            var existing = await db.PlayerStats
                .Where(s => finisherIds.Contains(s.UserId))
                .ToDictionaryAsync(s => s.UserId);

            PlayerStats StatsFor(string userId)
            {
                if (existing.TryGetValue(userId, out var s)) return s;
                s = new PlayerStats { UserId = userId };
                existing[userId] = s;
                db.PlayerStats.Add(s);
                return s;
            }

            var aiSurvived = snap.WinType == WinType.AiSurvival;
            foreach (var userId in finisherIds)
            {
                var s = StatsFor(userId);
                s.GamesPlayed++;
                if (aiSurvived)
                    s.AiSurvivalGamesWitnessed++;
            }

            // Reverse mode (phase 22): every time the AI correctly attributed one of a
            // player's answers to them this game, bump that player's TimesReadByAi.
            foreach (var p in snap.Players)
                if (p.TimesReadByAi > 0)
                    StatsFor(p.UserId).TimesReadByAi += p.TimesReadByAi;

            if (snap.WinType == WinType.Detector && snap.WinnerUserId != null)
                StatsFor(snap.WinnerUserId).DetectorWins++;

            if (aiSurvived)
                foreach (var userId in snap.WrongAccuserUserIds)
                    StatsFor(userId).TimesFooled++;

            // Crew game (phase 19): count the game against the crew.
            if (snap.CrewId != null)
            {
                var crew = await db.Crews.FirstOrDefaultAsync(c => c.Id == snap.CrewId.Value);
                if (crew != null) crew.GamesPlayed++;
            }

            await db.SaveChangesAsync();

            // Enforce the per-tier sample cap for everyone who just had answers harvested
            // (guest 10 / user 200) — trims oldest beyond the cap. Tier read off the
            // finisher rows we already loaded.
            var isGuestById = finisherUsers.ToDictionary(u => u.Id, u => u.IsGuest, StringComparer.Ordinal);
            foreach (var uid in harvestedUserIds)
            {
                var isGuest = isGuestById.TryGetValue(uid, out var g) && g;
                await SampleCaps.EnforceAsync(db, uid, isGuest);
            }

            // How often Gemini flaked this game (rate limits / errors → canned answers).
            if (snap.FallbackCount > 0)
                _logger.LogInformation(
                    "Game {Code} used {Count} fallback answer(s)", snap.JoinCode, snap.FallbackCount);

            // ---- fire-and-forget style summary job: the harvested answers just changed
            // each player's pool, so their profiles are now stale. Regenerate off the
            // engine loop; the summarizer swallows its own failures.
            if (harvestedUserIds.Count > 0)
            {
                var toRefresh = harvestedUserIds.ToList();
                _ = Task.Run(() => _summarizer.RefreshStaleProfilesAsync(toRefresh));
            }

            // ---- fire-and-forget GROUP profile update (phase 19): feed this crew game's
            // human answers (+ the stored prior) to the AI chain to refine the group notes.
            // Same discipline as the style job — off the loop, swallows its own failures.
            if (snap.CrewId != null)
            {
                var answerLines = snap.Messages
                    .Where(m => m.AuthorUserId != null && m.Text != "(no answer)")
                    .OrderBy(m => m.Round)
                    .Select(m => $"{m.DisplayName}: {m.Text}")
                    .ToList();
                if (answerLines.Count > 0)
                {
                    var crewId = snap.CrewId.Value;
                    _ = Task.Run(() => _groupProfiler.UpdateAfterGameAsync(crewId, answerLines));
                }
            }
        }
        catch (Exception ex)
        {
            // Persistence failing must never crash the engine loop. Log and move on;
            // the game already ended for the players.
            _logger.LogError(ex, "Persisting game {Code} failed", snap.JoinCode);
        }
    }
}
