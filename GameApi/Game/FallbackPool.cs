namespace GameApi.GameLoop;

// Section 6 of AI-DESIGN: canned answers used ONLY when the Gemini path fails
// (error / rate limit / empty after re-roll). Post-processing rules 4-6 still apply
// to whatever we pick, and the timing model still runs (no insta-send after a stall).
public static class FallbackPool
{
    public static readonly string[] Fallbacks =
    {
        "lol pass",
        "cant think of one rn",
        "hmm idk honestly",
        "oh thats a hard one",
        "probably something dumb",
        "i plead the fifth",
        "too many options lol",
        "my brain just went blank",
        "skip me on this one",
        "ill say what everyone else said",
        "no comment lmao",
        "this prompt is calling me out",
        "gonna keep that one private lol",
        "idk but someone here has a worse answer",
        "mines too embarrassing"
    };

    // Pick uniformly at random, never repeating one already used this game. If the
    // whole pool has been used, allow repeats. Records the pick + bumps the count.
    public static string Pick(FallbackState state, Random rng)
    {
        lock (state.Sync)
        {
            state.Count++;

            var available = Fallbacks.Where(f => !state.Used.Contains(f)).ToList();
            if (available.Count == 0)
            {
                // Exhausted — allow repeats.
                var any = Fallbacks[rng.Next(Fallbacks.Length)];
                return any;
            }

            var pick = available[rng.Next(available.Count)];
            state.Used.Add(pick);
            return pick;
        }
    }
}

// Per-lobby fallback bookkeeping: which canned answers have been used this game, and
// how many times we've had to fall back (logged so Kyle can see how often Gemini flaked).
public sealed class FallbackState
{
    public readonly object Sync = new();
    public readonly HashSet<string> Used = new(StringComparer.Ordinal);
    public int Count { get; set; }
}
