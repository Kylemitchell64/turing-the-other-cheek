using System.Security.Cryptography;

namespace GameApi.GameLoop;

// Stand-in AI for phase 3 (and the test suite). Returns a short, human-ish canned
// answer with a randomized delay so timing looks organic: mostly 5–25s, and ~15%
// of the time it deliberately lands in the last 5 seconds of the window.
//
// The delay is clamped to the actual time remaining so a shortened test window
// never pushes the answer past the deadline.
public class MockBrain : IAiBrain
{
    private static readonly string[] CannedAnswers =
    {
        "honestly no idea lol",
        "probably my phone tbh",
        "depends on the day",
        "yeah that one for sure",
        "not gonna lie kinda love it",
        "eh could go either way",
        "my dog would say the same",
        "way too much coffee",
        "still thinking about it",
        "the one from last week haha",
        "cant even pick one",
        "def a monday thing",
        "some cereal thing i think",
        "wish i had a better answer",
        "you already know it",
        "ask me again in an hour",
        "same as everyone else probably",
        "genuinely no clue",
        "that's a tough one ngl",
        "whatever's closest to the door",
    };

    public Task<AiAnswer> AnswerAsync(AiTurnContext context, CancellationToken ct)
    {
        var text = CannedAnswers[RandomNumberGenerator.GetInt32(CannedAnswers.Length)];

        var remaining = context.TimeRemaining;
        TimeSpan delay;

        // ~15% of rounds: aim for the last 5s (or the last portion of a short test
        // window). Otherwise a normal 5–25s human-looking delay.
        if (RandomNumberGenerator.GetInt32(100) < 15 && remaining > TimeSpan.FromSeconds(1))
        {
            var lastFive = TimeSpan.FromSeconds(Math.Min(5, remaining.TotalSeconds));
            var low = remaining - lastFive;
            var span = Math.Max(1, (int)(lastFive.TotalMilliseconds));
            delay = low + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(span));
        }
        else
        {
            var maxMs = (int)Math.Min(25_000, Math.Max(1, remaining.TotalMilliseconds));
            var minMs = Math.Min(5_000, maxMs - 1);
            if (minMs < 0) minMs = 0;
            var pick = minMs >= maxMs ? minMs : RandomNumberGenerator.GetInt32(minMs, maxMs);
            delay = TimeSpan.FromMilliseconds(pick);
        }

        // Never overshoot the window (leave a small margin).
        var cap = remaining - TimeSpan.FromMilliseconds(50);
        if (cap < TimeSpan.Zero) cap = TimeSpan.Zero;
        if (delay > cap) delay = cap;

        return Task.FromResult(new AiAnswer(text, delay));
    }
}
