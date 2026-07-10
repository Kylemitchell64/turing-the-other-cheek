namespace GameApi.GameLoop;

// Reverse mode (phase 22). The mirror image of IAiBrain: instead of the AI hiding among
// humans, it publicly reads the group and guesses who wrote which (shuffled, anonymous)
// answer. GeminiGuesser is the real impl (failover chain + strict-JSON parse + random
// fallback); MockGuesser backs the tests.

// One shuffled, anonymous answer the AI must attribute. Id is a stable per-round label
// ("a","b","c",...); Text is the answer exactly as written.
public record AnonAnswer(string Id, string Text);

// A confirmed prior-round attribution, fed back into the next round's prompt so the guesser
// learns across rounds: what it guessed for an answer and who actually wrote it.
public record PriorAttribution(int Round, string Text, string GuessedName, string ActualName, bool Correct);

// Everything the guesser needs for one reverse-mode reveal.
public record AiGuessContext(
    string Prompt,
    int RoundNumber,
    IReadOnlyList<AnonAnswer> Answers,
    // Every player's display name — the closed set the guesser must map each answer to.
    IReadOnlyList<string> PlayerNames,
    // Compact per-player style summaries ("NAME: {json}"), same lines classic feeds the
    // impostor. Empty when no profiles exist (start-gating should prevent that in reverse).
    IReadOnlyList<string> StyleSummaries,
    // Confirmed attributions from earlier rounds this game, so the guesser can adapt.
    IReadOnlyList<PriorAttribution> PriorAttributions);

// The guesser's verdict for one answer id: which player it thinks wrote it + a one-line
// friendly-roast taunt.
public record AiGuess(string AnswerId, string GuessedName, string Taunt);

public record AiGuessResult(IReadOnlyList<AiGuess> Guesses);

public interface IAiGuesser
{
    // Attribute every answer in the context to a player. MUST return a guess for every
    // answer id with a name from PlayerNames — real impls fall back to a random-but-valid
    // assignment rather than ever returning a partial/empty result, so the game never stalls.
    Task<AiGuessResult> GuessAsync(AiGuessContext context, CancellationToken ct);
}
