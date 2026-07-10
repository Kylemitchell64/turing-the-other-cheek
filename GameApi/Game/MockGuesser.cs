namespace GameApi.GameLoop;

// Stand-in reverse-mode guesser for the test suite (and any run without a provider key).
// Deterministic on purpose: it attributes EVERY anonymous answer to the first player in
// the list. That makes a reverse game's accuracy predictable in tests — exactly the one
// answer the first player actually wrote each round comes back "correct" — without ever
// touching a real model. Real attribution lives in GeminiGuesser.
public class MockGuesser : IAiGuesser
{
    public Task<AiGuessResult> GuessAsync(AiGuessContext context, CancellationToken ct)
    {
        var pick = context.PlayerNames.Count > 0 ? context.PlayerNames[0] : "someone";
        var guesses = context.Answers
            .Select(a => new AiGuess(a.Id, pick, "called it, this one's got your fingerprints all over it"))
            .ToList();
        return Task.FromResult(new AiGuessResult(guesses));
    }
}
