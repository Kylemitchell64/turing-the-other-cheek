using GameApi.GameLoop;
using Xunit;

namespace GameApi.Tests;

// Difficulty profiles (easy/normal/hard personas) + pace-scaled timing. The prompt
// checks pin which disguise pieces each mode keeps; the timing checks pin the
// window scaling, the easy fixed band, and the scrape gate.
public class DifficultyPaceTests
{
    private static AiTurnContext Ctx(string difficulty, double windowSeconds = 30.0) => new(
        CurrentPrompt: "worst purchase you ever made",
        RoundNumber: 2,
        AiDisplayName: "Riley",
        HumanDisplayNames: new[] { "Alex", "Sam" },
        History: new[]
        {
            new RoundHistory(1, "describe your morning in 5 words",
                new[] { new HistoryAnswer("Alex", "snoozed alarm, then coffee"),
                        new HistoryAnswer("Sam", "ran late as always") }),
        },
        PreviousOwnAnswers: new[] { "same, coffee then chaos" },
        StyleSummaries: new[] { "Alex: {\"avgLength\":28}", "Sam: {\"avgLength\":40}" },
        GroupStats: new GroupStats(30, 0.8, 0.1, 0.05),
        TimingState: new AnswerTiming.State(),
        FallbackState: new FallbackState(),
        TimeRemaining: TimeSpan.FromSeconds(windowSeconds),
        PackKey: "family",
        Difficulty: difficulty,
        WindowSeconds: windowSeconds);

    // ---- system prompt per difficulty ----

    [Fact]
    public void Hard_KeepsTheFullDisguisePrompt()
    {
        var prompt = GeminiBrain.BuildSystemPrompt(Ctx("hard"));
        Assert.Contains("statistical middle", prompt);
        Assert.Contains("Never mirror another player", prompt);
        Assert.Contains("THE PEOPLE YOU ARE IMITATING", prompt);
        Assert.Contains("Output ONLY the answer text", prompt);
    }

    [Fact]
    public void Normal_DropsTheSharpestRules_KeepsTheRest()
    {
        var prompt = GeminiBrain.BuildSystemPrompt(Ctx("normal"));
        Assert.DoesNotContain("statistical middle", prompt);          // rule 1 gone
        Assert.DoesNotContain("cant think of one tbh", prompt);       // rule 5 gone
        Assert.DoesNotContain("Never mirror another player", prompt); // rule 9 gone
        Assert.Contains("Match the group's median answer length", prompt); // rule 2 stays
        Assert.Contains("Output ONLY the answer text", prompt);       // rule 10 survives the trim
        Assert.Contains("THE PEOPLE YOU ARE IMITATING", prompt);      // style notes stay
    }

    [Fact]
    public void Easy_UsesThePolitePersona_NoStyleNotes_NoCountermeasures()
    {
        var prompt = GeminiBrain.BuildSystemPrompt(Ctx("easy"));
        Assert.Contains("fun party game with friends", prompt);
        Assert.Contains("polite, complete sentence", prompt);
        Assert.DoesNotContain("HOW TO NOT GET CAUGHT", prompt);
        Assert.DoesNotContain("THE PEOPLE YOU ARE IMITATING", prompt);
        Assert.Contains("R1 PROMPT: describe your morning in 5 words", prompt); // history still there
    }

    [Fact]
    public void ShortWindow_AddsTheTimePressureLine_EveryDifficulty()
    {
        foreach (var d in new[] { "easy", "normal", "hard" })
        {
            var prompt = GeminiBrain.BuildSystemPrompt(Ctx(d, windowSeconds: 10));
            Assert.Contains("TIME PRESSURE", prompt);
        }
        Assert.DoesNotContain("TIME PRESSURE", GeminiBrain.BuildSystemPrompt(Ctx("hard", windowSeconds: 30)));
    }

    // ---- profiles ----

    [Fact]
    public void ProfileFlags_MatchTheDesign()
    {
        var easy = DifficultyProfile.Get("easy");
        Assert.False(easy.UseStyleSummaries);
        Assert.False(easy.InjectTypos);
        Assert.False(easy.ConformToGroup);
        Assert.NotNull(easy.FixedTimingBand);

        var normal = DifficultyProfile.Get("normal");
        Assert.True(normal.UseStyleSummaries);
        Assert.False(normal.InjectTypos);
        Assert.False(normal.AllowDeadlineScrape);
        Assert.Null(normal.FixedTimingBand);

        var hard = DifficultyProfile.Get("hard");
        Assert.True(hard.InjectTypos);
        Assert.True(hard.AllowDeadlineScrape);
        Assert.Null(hard.FixedTimingBand);

        Assert.False(DifficultyProfile.IsValidKey("impossible"));
        Assert.True(PaceOptions.IsValidKey("snail"));
        Assert.False(PaceOptions.IsValidKey("warp"));
        Assert.Equal(10, PaceOptions.WindowSeconds("flash"));
        Assert.Equal(60, PaceOptions.WindowSeconds("snail"));
    }

    // ---- timing ----

    [Fact]
    public void EasyFixedBand_IsRoboticallyConsistent_AndInWindow()
    {
        var rng = new Random(42);
        var band = DifficultyProfile.Easy.FixedTimingBand;

        // 30s window: every delay lands in [16.5, 24].
        for (var i = 0; i < 50; i++)
        {
            var state = new AnswerTiming.State();
            var d = AnswerTiming.ComputeDelay(20, TimeSpan.FromSeconds(30), state, rng,
                windowSeconds: 30, allowDeadlineScrape: false, fixedBand: band).TotalSeconds;
            Assert.InRange(d, 30 * band!.Value.MinFrac - 0.01, 30 * band.Value.MaxFrac + 0.01);
        }

        // Flash 10s window: band scales down ([5.5, 8]) and always lands in-window.
        for (var i = 0; i < 50; i++)
        {
            var state = new AnswerTiming.State();
            var d = AnswerTiming.ComputeDelay(20, TimeSpan.FromSeconds(10), state, rng,
                windowSeconds: 10, allowDeadlineScrape: false, fixedBand: band).TotalSeconds;
            Assert.InRange(d, 4.0, 9.95);
        }
    }

    [Fact]
    public void ScrapeDisabled_NeverLandsInTheScrapeZone()
    {
        var rng = new Random(7);
        // With the scrape branch off, the gaussian band caps at 5/6 of the window
        // (25s for a 30s window) — nothing should land in the last-seconds zone.
        for (var i = 0; i < 300; i++)
        {
            var state = new AnswerTiming.State();
            var d = AnswerTiming.ComputeDelay(10, TimeSpan.FromSeconds(30), state, rng,
                windowSeconds: 30, allowDeadlineScrape: false).TotalSeconds;
            Assert.True(d <= 25.05, $"delay {d} landed past the gaussian band with scrape disabled");
        }
    }

    [Fact]
    public void GaussianBands_ScaleWithTheWindow()
    {
        var rng = new Random(11);
        // Snail (60s): delays spread across a band that would be impossible at 30s.
        var seenAbove30 = false;
        for (var i = 0; i < 300; i++)
        {
            var state = new AnswerTiming.State();
            var d = AnswerTiming.ComputeDelay(10, TimeSpan.FromSeconds(60), state, rng,
                windowSeconds: 60, allowDeadlineScrape: false).TotalSeconds;
            Assert.InRange(d, 4.0, 50.05); // band = [10, 50] with the 4s floor rule
            if (d > 30) seenAbove30 = true;
        }
        Assert.True(seenAbove30, "60s window never produced a delay past 30s — bands didn't scale");
    }

    [Fact]
    public void PostProcessing_FlagsGateConformanceAndTypos()
    {
        var rng = new Random(3);
        var stats = new GroupStats(60, 1.0, 0.0, 0.25); // group all-lowercase, never ends with period, typo-heavy

        // conformToGroup off: an uppercase start with a trailing period survives untouched.
        for (var i = 0; i < 20; i++)
        {
            var kept = AnswerPostProcessor.ProcessAsync(
                "My answer is simple.", stats, rng, reroll: null, CancellationToken.None,
                conformToGroup: false, injectTypos: false).Result;
            Assert.Equal("My answer is simple.", kept);
        }
    }
}
