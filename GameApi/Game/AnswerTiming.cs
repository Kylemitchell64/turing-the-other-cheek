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
    // windowSeconds is the full answer window for this lobby's pace — the human-ish
    // gaussian and its bands scale with it (the 30s defaults reproduce the original
    // mu=13/sigma=5 clamped [5,25] behavior). allowDeadlineScrape gates the
    // last-seconds branch. fixedBand (easy mode) ignores all of that and lands in a
    // tight fraction-of-window band every round — consistency is the tell.
    public static TimeSpan ComputeDelay(int answerLength, TimeSpan timeRemaining, State state, Random rng,
        double windowSeconds = 30.0, bool allowDeadlineScrape = true,
        (double MinFrac, double MaxFrac)? fixedBand = null)
    {
        var remaining = timeRemaining.TotalSeconds;
        if (remaining <= 0) return TimeSpan.Zero;

        if (windowSeconds < 6.0) windowSeconds = 6.0;
        var typingFloor = 2.0 + answerLength / 7.0;

        // The window may be short (compressed test timings). Cap everything to what's left.
        var deadline = remaining;

        // Easy mode: uniform inside the fixed band, no anti-pattern memory games —
        // a robotically steady cadence round after round.
        if (fixedBand is { } band)
        {
            var lo = windowSeconds * band.MinFrac;
            var hi = windowSeconds * band.MaxFrac;
            var fixedDelay = lo + rng.NextDouble() * (hi - lo);
            fixedDelay = ClampToWindow(fixedDelay, deadline);
            Remember(state, fixedDelay);
            return TimeSpan.FromSeconds(fixedDelay);
        }

        // Scale the human bands to the window. At 30s: lo=5, hi=25, mu=13.5, sigma=5.1.
        var bandLo = windowSeconds / 6.0;
        var bandHi = windowSeconds * 5.0 / 6.0;
        var mu = windowSeconds * 0.45;
        var sigma = windowSeconds * 0.17;

        double delay;
        var r = rng.NextDouble();
        var forceOpposite = AntiPatternForce(state, windowSeconds);

        if (allowDeadlineScrape && r < 0.15)
        {
            // Deadline scrape: land 2.0-4.5s before the deadline.
            var scrape = 2.0 + rng.NextDouble() * 2.5;
            delay = deadline - scrape;
        }
        else
        {
            var gaussian = Gaussian(rng, mu, sigma);
            gaussian = Math.Clamp(gaussian, bandLo, bandHi);
            delay = Math.Max(typingFloor, gaussian);
        }

        // Anti-pattern guard: if the last three delays clustered, shove this one to
        // the opposite half of the band.
        if (forceOpposite.HasValue)
        {
            var mid = (bandLo + bandHi) / 2.0;
            delay = forceOpposite.Value == Half.Low
                ? bandLo + rng.NextDouble() * (mid - bandLo)
                : mid + rng.NextDouble() * (bandHi - mid);
            delay = Math.Max(typingFloor, delay);
        }

        delay = ClampToWindow(delay, deadline);

        Remember(state, delay);
        return TimeSpan.FromSeconds(delay);
    }

    // Never in the first 4 seconds; never past the deadline (hair of margin so it
    // still lands in-window). The 4s floor wins only up to what's left.
    private static double ClampToWindow(double delay, double deadline)
    {
        if (delay < 4.0) delay = 4.0;
        var cap = deadline - 0.05;
        if (cap < 0) cap = 0;
        if (delay > cap) delay = cap;
        if (delay < 4.0 && cap >= 4.0) delay = 4.0;
        return delay;
    }

    // How long the AI should appear to be "typing" before it submits — the lead time
    // for its fake typing indicator. Mirrors the typing-floor idea (length / 7, min 2s)
    // plus a little jitter so the lead is never metronomic. This only drives the
    // PlayerTyping bubble; the actual submit delay still comes from ComputeDelay. A
    // human's indicator reflects exactly what they typed, so the AI reusing the same
    // floor math keeps it indistinguishable.
    public static TimeSpan TypingDuration(int answerLength, Random rng)
    {
        var secs = Math.Max(2.0, answerLength / 7.0) + rng.NextDouble() * 1.5;
        return TimeSpan.FromSeconds(secs);
    }

    private enum Half { Low, High }

    // If the last 3 recorded delays are all within +-3s of each other, force the next
    // roll into whichever half of the window's band is farther from that cluster.
    private static Half? AntiPatternForce(State state, double windowSeconds)
    {
        var recent = state.RecentDelaySeconds;
        if (recent.Count < 3) return null;
        var last3 = recent.Skip(recent.Count - 3).ToList();
        var min = last3.Min();
        var max = last3.Max();
        if (max - min > 3.0) return null;

        var center = (min + max) / 2.0;
        // Cluster in the low half -> push high, and vice versa.
        return center < windowSeconds / 2.0 ? Half.High : Half.Low;
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
