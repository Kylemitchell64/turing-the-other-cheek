using GameApi.GameLoop;
using Xunit;

namespace GameApi.Tests;

// Unit tests for AI-DESIGN section 4 timing bounds: never under 4s, never past the
// deadline, and the typing floor (2 + len/7) respected on normal (non-scrape) rolls.
public class TimingModelTests
{
    [Fact]
    public void NeverSubmitsInFirst4Seconds()
    {
        // Long window so the 4s floor is the only thing that can bind.
        var window = TimeSpan.FromSeconds(30);
        for (var seed = 0; seed < 500; seed++)
        {
            var state = new AnswerTiming.State();
            var delay = AnswerTiming.ComputeDelay(20, window, state, new Random(seed));
            Assert.True(delay >= TimeSpan.FromSeconds(4),
                $"seed {seed}: {delay.TotalSeconds}s < 4s");
        }
    }

    [Fact]
    public void NeverExceedsDeadline()
    {
        foreach (var secs in new[] { 6.0, 12.0, 30.0 })
        {
            var window = TimeSpan.FromSeconds(secs);
            for (var seed = 0; seed < 500; seed++)
            {
                var state = new AnswerTiming.State();
                var delay = AnswerTiming.ComputeDelay(15, window, state, new Random(seed));
                Assert.True(delay <= window, $"window {secs}s seed {seed}: {delay.TotalSeconds}s > window");
            }
        }
    }

    [Fact]
    public void TypingFloorRespected_OnNormalRolls()
    {
        // A long answer pushes the typing floor above the 4s floor. On non-scrape rolls
        // the delay must be at least the typing floor (2 + len/7), within the window.
        const int len = 210; // floor = 2 + 210/7 = 32s
        var floor = TimeSpan.FromSeconds(2 + len / 7.0);
        var window = TimeSpan.FromSeconds(60); // comfortably larger than the floor

        var normalRollHit = false;
        for (var seed = 0; seed < 500; seed++)
        {
            var state = new AnswerTiming.State();
            var delay = AnswerTiming.ComputeDelay(len, window, state, new Random(seed));
            // Scrape rolls (r<0.15) intentionally land near the deadline and are exempt
            // from the floor; only assert the floor on rolls that aren't near-deadline.
            if (delay < window - TimeSpan.FromSeconds(5))
            {
                normalRollHit = true;
                Assert.True(delay >= floor - TimeSpan.FromMilliseconds(1),
                    $"seed {seed}: {delay.TotalSeconds}s < floor {floor.TotalSeconds}s");
            }
        }
        Assert.True(normalRollHit, "expected at least one non-scrape roll to exercise the floor");
    }

    [Fact]
    public void AntiPatternGuard_BreaksUpClusteredDelays()
    {
        // Feed a state whose last 3 delays are tightly clustered low; the next roll
        // must be forced to the high half (>= 15 - typing considerations).
        var state = new AnswerTiming.State();
        state.RecentDelaySeconds.AddRange(new[] { 6.0, 7.0, 6.5 }); // clustered low
        var window = TimeSpan.FromSeconds(60);

        var delay = AnswerTiming.ComputeDelay(10, window, state, new Random(0));
        Assert.True(delay.TotalSeconds >= 15.0 - 0.001,
            $"expected forced high-half delay, got {delay.TotalSeconds}s");
    }

    [Fact]
    public void ShortWindow_StillNeverExceedsDeadline()
    {
        // Compressed test-style window (2s): delay clamps into the window, may be < 4s
        // because the window itself is shorter than the floor — but never over.
        var window = TimeSpan.FromSeconds(2);
        for (var seed = 0; seed < 200; seed++)
        {
            var delay = AnswerTiming.ComputeDelay(15, window, new AnswerTiming.State(), new Random(seed));
            Assert.True(delay <= window, $"seed {seed}: {delay.TotalSeconds}s > 2s window");
            Assert.True(delay >= TimeSpan.Zero);
        }
    }
}
