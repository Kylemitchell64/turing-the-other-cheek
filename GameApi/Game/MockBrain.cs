using System.Security.Cryptography;

namespace GameApi.GameLoop;

// Stand-in AI for the test suite (and any run without a Gemini key). Returns a short,
// human-ish canned answer and sizes the send delay with the SHARED timing model
// (AnswerTiming, AI-DESIGN section 4) — same code path GeminiBrain uses — so the
// mock's cadence matches production behavior.
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
        var profile = DifficultyProfile.Get(context.Difficulty);
        var delay = AnswerTiming.ComputeDelay(
            text.Length, context.TimeRemaining, context.TimingState, Random.Shared,
            windowSeconds: context.WindowSeconds,
            allowDeadlineScrape: profile.AllowDeadlineScrape,
            fixedBand: profile.FixedTimingBand);
        return Task.FromResult(new AiAnswer(text, delay));
    }
}
