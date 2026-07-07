using GameApi.GameLoop;
using Xunit;

namespace GameApi.Tests;

// Unit tests for AI-DESIGN section 2 (post-processing pipeline) and section 4
// (timing model). No Gemini dependency: the re-roll step is exercised with a stub
// callback, and everything else is deterministic with a seeded Random.
public class PostProcessingTests
{
    private static GroupStats Lowercase(double median = 40) =>
        new(MedianAnswerLength: median, LowercaseStartRate: 0.9, TrailingPeriodRate: 0.1, MeanTypoRate: 0.0);

    // ---- step 1: strip wrapping quotes + first line only ----

    [Theory]
    [InlineData("\"hello there\"", "hello there")]
    [InlineData("`code answer`", "code answer")]
    [InlineData("'single quoted'", "single quoted")]
    [InlineData("first line\nsecond line", "first line")]
    public void Step1_StripsQuotesAndMultiline(string raw, string expected)
    {
        Assert.Equal(expected, AnswerPostProcessor.Step1StripAndFirstLine(raw));
    }

    // ---- step 2: hard clamp ----

    [Fact]
    public void Step2_ClampsOver240ToBoundary()
    {
        var longText = new string('a', 200) + ", and then some more words here to push it well over the limit for sure.";
        Assert.True(longText.Length > 240);
        var clamped = AnswerPostProcessor.Step2Clamp(longText, groupMedian: 1000);
        Assert.True(clamped.Length <= 240);
    }

    [Fact]
    public void Step2_ClampsToGroupMedianTimes2Point5()
    {
        var text = "this is a fairly long answer that exceeds two and a half times the tiny group median length";
        var clamped = AnswerPostProcessor.Step2Clamp(text, groupMedian: 10); // hardMax = 25
        Assert.True(clamped.Length <= 25);
    }

    // ---- step 3: banned substrings, re-roll policy ----

    [Fact]
    public async Task Step3_EmDash_TriggersReroll_AndUsesCleanReroll()
    {
        var rng = new Random(1);
        var rerollCalls = 0;
        var result = await AnswerPostProcessor.ProcessAsync(
            raw: "coffee first — then chaos",
            stats: Lowercase(),
            rng: rng,
            reroll: (suffix, ct) =>
            {
                rerollCalls++;
                Assert.Equal(AnswerPostProcessor.RerollSuffix, suffix);
                return Task.FromResult("coffee then chaos");
            },
            ct: CancellationToken.None);

        Assert.Equal(1, rerollCalls);
        Assert.DoesNotContain("—", result);
        Assert.Contains("coffee", result);
    }

    [Fact]
    public async Task Step3_RerollAlsoBanned_StripsInsteadOfLooping()
    {
        var rng = new Random(2);
        var rerollCalls = 0;
        var result = await AnswerPostProcessor.ProcessAsync(
            raw: "lets delve into it",
            stats: Lowercase(),
            rng: rng,
            reroll: (suffix, ct) =>
            {
                rerollCalls++;
                return Task.FromResult("still gonna delve deeper"); // re-roll also fails
            },
            ct: CancellationToken.None);

        Assert.Equal(1, rerollCalls); // exactly one re-roll, never loops
        Assert.DoesNotContain("delve", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Step3_NoRerollAvailable_StripsBanned()
    {
        var rng = new Random(3);
        var result = await AnswerPostProcessor.ProcessAsync(
            raw: "as an AI I think olives",
            stats: Lowercase(),
            rng: rng,
            reroll: null,
            ct: CancellationToken.None);

        Assert.DoesNotContain("as an AI", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Step3_CleanAnswer_DoesNotReroll()
    {
        var rerollCalls = 0;
        var result = await AnswerPostProcessor.ProcessAsync(
            raw: "some gym membership i used twice lol",
            stats: Lowercase(),
            rng: new Random(4),
            reroll: (s, ct) => { rerollCalls++; return Task.FromResult(""); },
            ct: CancellationToken.None);

        Assert.Equal(0, rerollCalls);
        Assert.Contains("gym membership", result);
    }

    // ---- step 4: case conformance ----

    [Fact]
    public void Step4_LowercasesFirstChar_WhenGroupIsLowercase()
    {
        // rate 0.9 >= 0.6; with a fixed seed the probability draw lands under 0.9.
        var seedThatLowercases = FindSeed(rng =>
            AnswerPostProcessor.Step4Case("Coffee then chaos", 0.9, rng).StartsWith("c"));
        var text = AnswerPostProcessor.Step4Case("Coffee then chaos", 0.9, new Random(seedThatLowercases));
        Assert.StartsWith("c", text);
    }

    [Fact]
    public void Step4_LeavesUppercase_WhenGroupRateBelowThreshold()
    {
        // rate 0.4 < 0.6 → never touched, regardless of seed.
        for (var seed = 0; seed < 50; seed++)
        {
            var text = AnswerPostProcessor.Step4Case("Coffee", 0.4, new Random(seed));
            Assert.StartsWith("C", text);
        }
    }

    // ---- step 5: trailing period ----

    [Fact]
    public void Step5_StripsTrailingPeriod_WhenGroupRarelyUsesThem()
    {
        var seed = FindSeed(rng => !AnswerPostProcessor.Step5TrailingPeriod("done.", 0.1, rng).EndsWith('.'));
        var text = AnswerPostProcessor.Step5TrailingPeriod("done.", 0.1, new Random(seed));
        Assert.False(text.EndsWith('.'));
    }

    [Fact]
    public void Step5_KeepsTrailingPeriod_WhenGroupUsesThem()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var text = AnswerPostProcessor.Step5TrailingPeriod("done.", 0.7, new Random(seed));
            Assert.EndsWith(".", text);
        }
    }

    // ---- step 6: typo injection determinism ----

    [Fact]
    public void Step6_IsDeterministic_ForAGivenSeed()
    {
        // Same seed → same output every time (determinism the game relies on for tests).
        var a = AnswerPostProcessor.Step6Typo("probably the gym membership honestly", 0.25, new Random(99));
        var b = AnswerPostProcessor.Step6Typo("probably the gym membership honestly", 0.25, new Random(99));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Step6_NeverTypesTheFirstWord()
    {
        // Across many seeds, the first word is always intact.
        const string text = "coffee then some cereal thing i think";
        var firstWord = text.Split(' ')[0];
        for (var seed = 0; seed < 200; seed++)
        {
            var outp = AnswerPostProcessor.Step6Typo(text, 0.25, new Random(seed));
            Assert.StartsWith(firstWord + " ", outp);
        }
    }

    [Fact]
    public void Step6_RateIsClampedToAtLeast5Percent()
    {
        // Section 6: p = clamp(rate, 0.05, 0.25), so even a 0.0 group rate injects
        // rarely (~5%). Most seeds leave the text untouched; a few inject one typo.
        const string text = "probably the gym membership honestly";
        var untouched = 0;
        var injected = 0;
        for (var seed = 0; seed < 200; seed++)
        {
            var outp = AnswerPostProcessor.Step6Typo(text, 0.0, new Random(seed));
            if (outp == text) untouched++; else injected++;
        }
        Assert.True(untouched > injected, "at ~5% most answers should be untouched");
        Assert.True(untouched >= 150, $"expected the large majority untouched, got {untouched}/200");
    }

    // ---- helper: find a seed producing a desired probabilistic outcome ----
    private static int FindSeed(Func<Random, bool> predicate)
    {
        for (var seed = 0; seed < 10000; seed++)
            if (predicate(new Random(seed))) return seed;
        throw new Xunit.Sdk.XunitException("no seed satisfied the predicate");
    }
}
