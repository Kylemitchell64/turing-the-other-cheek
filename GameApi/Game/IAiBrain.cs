namespace GameApi.GameLoop;

// One answer the AI has "written" for the current prompt, plus how long to wait
// before it lands so the timing reads human.
public record AiAnswer(string Text, TimeSpan Delay);

// A prior round's answers, for context. Keyed by display name — the brain never
// learns which of these was itself (it doesn't need to, and we don't leak it).
public record RoundHistory(int Round, string Prompt, IReadOnlyList<HistoryAnswer> Answers);

public record HistoryAnswer(string DisplayName, string Text);

// Everything the brain needs to produce this round's answer. Phase 4's GeminiBrain
// will feed most of this straight into the model prompt; MockBrain ignores it.
public record AiTurnContext(
    string CurrentPrompt,
    int RoundNumber,
    string AiDisplayName,
    IReadOnlyList<string> HumanDisplayNames,
    IReadOnlyList<RoundHistory> History,
    // Compact per-human style summaries (StyleProfiles.SummaryJson) once phase 6
    // wires them in. Empty for now.
    IReadOnlyList<string> StyleSummaries,
    // Time left in the prompt window when we asked — the brain sizes its delay so
    // the answer still lands before the deadline.
    TimeSpan TimeRemaining);

// The AI player. Phase 4 adds GeminiBrain (real API, backoff, canned fallback);
// this phase ships MockBrain only. Async + cancellation so a real HTTP call can be
// aborted if the round ends early.
public interface IAiBrain
{
    Task<AiAnswer> AnswerAsync(AiTurnContext context, CancellationToken ct);
}
