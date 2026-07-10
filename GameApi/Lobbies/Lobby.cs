using GameApi.GameLoop;
using GameApi.Models;

namespace GameApi.Lobbies;

// In-memory lobby + live game state. NOT an EF entity — everything here lives in
// memory until it's persisted to the DB at game END (see GameEngine.PersistAsync).
// One lock object per lobby (Sync) guards every mutation; the hub and the engine
// both take it before touching anything.
public class Lobby
{
    public string Code { get; init; } = default!;
    public string HostUserId { get; set; } = default!;
    public GameState State { get; set; } = GameState.Lobby;

    // The lock every hub method / engine tick takes before touching this lobby.
    public object Sync { get; } = new();

    public List<LobbyPlayer> Players { get; } = new();

    // Set on StartGame. The AI's display name (already collision-checked). Sent to
    // clients in the roster like any human — never flagged as special until the game ends.
    public string? AiDisplayName { get; set; }

    // Which prompt pack this lobby plays. Host picks it pre-start via SetLobbyOptions;
    // defaults to family. Kept across a rematch (only the used-prompt set resets).
    public string PackKey { get; set; } = PromptPacks.DefaultKey;

    // How sneaky the impostor plays (easy|normal|hard) and how long everyone gets to
    // answer (flash|quick|standard|relaxed|snail). Host-picked with the pack, kept
    // across a rematch like PackKey (ResetForNewGame doesn't touch them).
    public string Difficulty { get; set; } = DifficultyProfile.DefaultKey;
    public string PaceKey { get; set; } = PaceOptions.DefaultKey;

    // Prompt indices already used this game, so a pack's prompts never repeat within
    // one game (cleared on a fresh game / rematch, and when a pack is exhausted).
    public HashSet<int> UsedPromptIndices { get; } = new();

    public LobbyPlayer? FindPlayer(string userId) =>
        Players.FirstOrDefault(p => p.UserId == userId);

    public LobbyPlayer? FindPlayerByName(string displayName) =>
        Players.FirstOrDefault(p =>
            string.Equals(p.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));

    // ---- live round state (Phase 3) ----

    public int RoundNumber { get; set; }
    public string CurrentPrompt { get; set; } = "";

    // Each round's prompt text, keyed by round number. Kept so the AI history and the
    // end transcript render the correct "R1 PROMPT: ..." per round (not just the
    // latest prompt), and so it can be persisted to the game record.
    public Dictionary<int, string> RoundPrompts { get; } = new();

    // When the current phase's server-side deadline expires (UTC). The engine ticks
    // and compares against this — the client clock is never trusted.
    public DateTime PhaseDeadlineUtc { get; set; }

    // Answers for the current round, keyed by display name (humans + the AI).
    public Dictionary<string, RoundAnswer> Answers { get; } = new(StringComparer.Ordinal);

    // Full annotated history across all rounds, for the AI context + end transcript.
    public List<RecordedAnswer> Transcript { get; } = new();

    // Set true once we've kicked off the AI's answer task for this round so we don't
    // double-fire it on subsequent ticks.
    public bool AiAnswerRequested { get; set; }

    // Who's currently shown as "typing" this round (display names, humans + the AI).
    // Humans join/leave via the SetTyping hub method; the AI's fake-typing task adds
    // and removes itself. Cleared at phase end so no bubble lingers past Prompting.
    public HashSet<string> TypingNames { get; } = new(StringComparer.Ordinal);

    // Per-lobby AI state that persists across rounds: the timing anti-pattern memory
    // (last 3 delays) and the fallback used-set + count. Reset each new game.
    public AnswerTiming.State TimingState { get; private set; } = new();
    public FallbackState FallbackState { get; private set; } = new();

    // Rendered per-human style-summary lines ("NAME: {json}"), loaded once from the
    // StyleProfiles table at game start. Empty when no profiles exist. Injected into
    // the AI system prompt (AI-DESIGN section 1).
    public List<string> StyleSummaries { get; } = new();

    // ---- accusation / veto / cooldown state ----

    // The locked-in accusation for the current Accusing round (first one wins).
    public string? AccuserName { get; set; }
    public string? AccusedName { get; set; }

    // During a veto window, which token-holders (by display name) are allowed to
    // veto — everyone with >=1 token except the accuser.
    public HashSet<string> VetoEligible { get; } = new(StringComparer.Ordinal);

    // Cooldown: after a veto, ONE full prompt round plays with NO accusation window
    // for anyone. This is the round number during which accusations are blacked out.
    public int? BlackoutRound { get; set; }

    // The vetoer gets a 5s exclusive priority accusation window in the round AFTER
    // the cooldown round. This holds their display name for that one round.
    public string? PriorityVetoerName { get; set; }
    public int? PriorityRound { get; set; }

    // Within an Accusing phase, whether we're currently in the vetoer's exclusive
    // priority sub-window (before the general window opens).
    public bool InPriorityWindow { get; set; }

    // UserIds of players who made at least one WRONG accusation this game. On an AI
    // survival win, every one of them gets +1 TimesFooled (spec's rule). Set of user
    // ids so a player who whiffs twice is only counted once.
    public HashSet<string> WrongAccuserUserIds { get; } = new(StringComparer.Ordinal);

    // Result state for the game, filled at end.
    public WinType WinType { get; set; } = WinType.None;
    public string? WinnerUserId { get; set; }
    public string? WinnerName { get; set; }

    public DateTime StartedAtUtc { get; set; }

    // Reset all per-game state for a fresh game / rematch in the same lobby.
    public void ResetForNewGame()
    {
        State = GameState.Lobby;
        AiDisplayName = null;
        // PackKey intentionally preserved — a rematch keeps the host's chosen pack.
        UsedPromptIndices.Clear();
        RoundNumber = 0;
        CurrentPrompt = "";
        PhaseDeadlineUtc = default;
        Answers.Clear();
        RoundPrompts.Clear();
        Transcript.Clear();
        AiAnswerRequested = false;
        TypingNames.Clear();
        TimingState = new();
        FallbackState = new();
        StyleSummaries.Clear();
        AccuserName = null;
        AccusedName = null;
        VetoEligible.Clear();
        WrongAccuserUserIds.Clear();
        BlackoutRound = null;
        PriorityVetoerName = null;
        PriorityRound = null;
        InPriorityWindow = false;
        WinType = WinType.None;
        WinnerUserId = null;
        WinnerName = null;
        StartedAtUtc = default;

        foreach (var p in Players)
        {
            p.TokensRemaining = 3;
            p.IsEliminated = false;
            p.VetoerCount = 0;
        }
    }
}

// An answer captured during the live round.
public class RoundAnswer
{
    public string Text { get; set; } = "";
    public bool IsAi { get; set; }
}

// A permanent record of one answer, kept for the AI context and the end transcript.
// AuthorUserId null == the AI (only used at DB-persist / end time, never sent live).
public class RecordedAnswer
{
    public int Round { get; set; }
    public string DisplayName { get; set; } = "";
    public string? AuthorUserId { get; set; }
    public string Text { get; set; } = "";
    public bool IsAi { get; set; }
    public DateTime SentAtUtc { get; set; }
}

// A human seat in the lobby. The AI is NOT a LobbyPlayer — it has no userId /
// connection and only exists as a name in the roster once the game starts.
public class LobbyPlayer
{
    public string UserId { get; init; } = default!;
    public string DisplayName { get; init; } = default!;

    // The player's saved character JSON, cached from the DB when they take a seat, so the
    // roster payloads can carry a config without a DB hit per broadcast. Null == none
    // saved (or the DB was unreachable) → the name-hash default is used instead.
    public string? CharacterJson { get; set; }

    // A player can hold multiple connections briefly (reconnect before the old
    // socket times out). Empty set == currently disconnected.
    public HashSet<string> ConnectionIds { get; } = new();

    public int TokensRemaining { get; set; } = 3;

    // Accusation-eliminated: a wrong accusation at 0 tokens. They still see the game
    // and answer prompts, but can't accuse OR veto (answer-only). Spec's rule.
    public bool IsEliminated { get; set; }

    public int VetoerCount { get; set; }

    public bool IsConnected => ConnectionIds.Count > 0;

    // Can this player still veto / be offered a veto? Needs a token and not eliminated.
    public bool CanVeto => TokensRemaining > 0 && !IsEliminated;
}
