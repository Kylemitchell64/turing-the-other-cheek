namespace GameApi.GameLoop;

// Section 4 of AI-DESIGN: the server-side per-answer timing model. Both brains use
// this so the AI's send cadence reads human whether the text came from Gemini or
// the mock. State (last 3 delays, for the anti-pattern guard) is per-lobby, held by
// the caller and threaded back in each round.
//
// Rules:
//   typingFloor = 2 + answerLength / 7.0  (delay never below this)
//   r < 0.15  -> deadline scrape: sendAt = deadline - Uniform(2.0, 4.5)s
//   else      -> sendAt = phaseStart + max(typingFloor, Gaussian(mu=13, sigma=5) clamped [5,25])s
//   anti-pattern: if last 3 delays all within +-3s of each other, force this roll to the opposite half.
//   never submit in the first 4 seconds, regardless of length.
public static class AnswerTiming
{
    // Per-lobby delay memory for the cross-round anti-pattern guard.
    public sealed class State
    {
        public readonly List<double> RecentDelaySeconds = new();
    }

    // Compute the delay (from phase start / "now") before this answer should land.
    // timeRemaining is how much of the prompt window is left when we ask.
    public static TimeSpan ComputeDelay(int answerLength, TimeSpan timeRemaining, State state, Random rng)
    {
        var remaining = timeRemaining.TotalSeconds;
        if (remaining <= 0) return TimeSpan.Zero;

        var typingFloor = 2.0 + answerLength / 7.0;

        // The window may be short (compressed test timings). Cap everything to what's left.
        var deadline = remaining;

        double delay;
        var r = rng.NextDouble();
        var forceOpposite = AntiPatternForce(state);

        if (r < 0.15)
        {
            // Deadline scrape: land 2.0-4.5s before the deadline.
            var scrape = 2.0 + rng.NextDouble() * 2.5;
            delay = deadline - scrape;
        }
        else
        {
            var gaussian = Gaussian(rng, 13.0, 5.0);
            gaussian = Math.Clamp(gaussian, 5.0, 25.0);
            delay = Math.Max(typingFloor, gaussian);
        }

        // Anti-pattern guard: if the last three delays clustered, shove this one to
        // the opposite half of the [5,25] band.
        if (forceOpposite.HasValue)
        {
            delay = forceOpposite.Value == Half.Low
                ? 5.0 + rng.NextDouble() * 10.0   // [5, 15)
                : 15.0 + rng.NextDouble() * 10.0; // [15, 25)
            delay = Math.Max(typingFloor, delay);
        }

        // Never in the first 4 seconds.
        if (delay < 4.0) delay = 4.0;
        // Never past the deadline (leave a hair of margin so it still lands in-window).
        var cap = deadline - 0.05;
        if (cap < 0) cap = 0;
        if (delay > cap) delay = cap;
        // The 4s floor still wins over a tiny window only up to what's left.
        if (delay < 4.0 && cap >= 4.0) delay = 4.0;

        Remember(state, delay);
        return TimeSpan.FromSeconds(delay);
    }

    private enum Half { Low, High }

    // If the last 3 recorded delays are all within +-3s of each other, force the next
    // roll into whichever half of [5,25] is farther from that cluster.
    private static Half? AntiPatternForce(State state)
    {
        var recent = state.RecentDelaySeconds;
        if (recent.Count < 3) return null;
        var last3 = recent.Skip(recent.Count - 3).ToList();
        var min = last3.Min();
        var max = last3.Max();
        if (max - min > 3.0) return null;

        var center = (min + max) / 2.0;
        // Cluster in the low half -> push high, and vice versa.
        return center < 15.0 ? Half.High : Half.Low;
    }

    private static void Remember(State state, double delaySeconds)
    {
        state.RecentDelaySeconds.Add(delaySeconds);
        if (state.RecentDelaySeconds.Count > 3)
            state.RecentDelaySeconds.RemoveAt(0);
    }

    // Box-Muller normal sample.
    private static double Gaussian(Random rng, double mean, double stdDev)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return mean + stdDev * z;
    }
}
