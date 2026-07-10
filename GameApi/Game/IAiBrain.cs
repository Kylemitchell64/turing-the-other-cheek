namespace GameApi.GameLoop;

// One answer the AI has "written" for the current prompt, plus how long to wait
// before it lands so the timing reads human.
public record AiAnswer(string Text, TimeSpan Delay);

// A prior round's answers, for context. Keyed by display name — the brain never
// learns which of these was itself (it doesn't need to, and we don't leak it).
public record RoundHistory(int Round, string Prompt, IReadOnlyList<HistoryAnswer> Answers);

public record HistoryAnswer(string DisplayName, string Text);

// Statistics about the group's answers so far, driving post-processing (case /
// trailing-period / typo conformance) and the timing median clamp. Computed by the
// engine from every human+AI answer recorded to date. See AI-DESIGN sections 2 & 4.
public record GroupStats(
    double MedianAnswerLength,
    double LowercaseStartRate,
    double TrailingPeriodRate,
    double MeanTypoRate);

// Everything the brain needs to produce this round's answer. GeminiBrain feeds most
// of this into the model prompt; MockBrain ignores the text fields but still uses
// the group stats + timing state to size its delay.
public record AiTurnContext(
    string CurrentPrompt,
    int RoundNumber,
    string AiDisplayName,
    IReadOnlyList<string> HumanDisplayNames,
    IReadOnlyList<RoundHistory> History,
    // The AI's own prior answers (newline-joined into the prompt), for continuity.
    IReadOnlyList<string> PreviousOwnAnswers,
    // Compact per-human style summaries: one "NAME: {json}" line each. From
    // StyleProfiles.SummaryJson (phase 6); empty when no profiles exist yet.
    IReadOnlyList<string> StyleSummaries,
    // Group-answer statistics for post-processing + timing.
    GroupStats GroupStats,
    // Timing anti-pattern memory for this lobby (last 3 delays). Mutated in place.
    AnswerTiming.State TimingState,
    // Per-lobby fallback bookkeeping (used set + count), for the canned-answer path.
    FallbackState FallbackState,
    // Time left in the prompt window when we asked — the brain sizes its delay so
    // the answer still lands before the deadline.
    TimeSpan TimeRemaining,
    // Which prompt pack this game is using. Drives one additive, pack-conditional
    // line in the system prompt (trivia = guess like a human; adult/drinking = match
    // the group's crassness, never escalate). Defaults to family (no extra line).
    string PackKey = "family",
    // Impostor difficulty (easy|normal|hard) — which parts of the disguise kit are
    // on. See DifficultyProfile.
    string Difficulty = DifficultyProfile.DefaultKey,
    // The full answer window for this lobby's pace, so the timing model scales its
    // human-ish bands to it (TimeRemaining is just what's LEFT when we were asked).
    double WindowSeconds = 30.0,
    // For a custom pack (phase 20): whether the host's pack was tagged adult. Drives the
    // same crude-match guidance line the adult/drinking packs get; false leaves it off.
    bool CustomNsfw = false,
    // Rendered GROUP NOTES for a crew game (phase 19): what the AI has learned about how
    // this exact group plays. Injected into the system prompt verbatim, only when non-null.
    // Set only on NORMAL/HARD crew games — EASY never gets it, ordinary lobbies leave it null.
    string? GroupNotes = null);

// The AI player. GeminiBrain is the real impl (API, backoff, canned fallback);
// MockBrain backs the tests. Async + cancellation so a real HTTP call can be
// aborted if the round ends early.
public interface IAiBrain
{
    Task<AiAnswer> AnswerAsync(AiTurnContext context, CancellationToken ct);
}
