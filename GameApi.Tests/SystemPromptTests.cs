using GameApi.GameLoop;
using Xunit;

namespace GameApi.Tests;

// Verifies the AI system prompt renders per AI-DESIGN section 1: chat history as
// "R1 PROMPT: ...", style-summary lines when profiles exist, and the whole style
// section omitted when they don't.
public class SystemPromptTests
{
    private static AiTurnContext Ctx(IReadOnlyList<string> styleSummaries) => new(
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
        StyleSummaries: styleSummaries,
        GroupStats: new GroupStats(30, 0.8, 0.1, 0.05),
        TimingState: new AnswerTiming.State(),
        FallbackState: new FallbackState(),
        TimeRemaining: TimeSpan.FromSeconds(20));

    [Fact]
    public void RendersChatHistoryWithPerRoundPromptLabel()
    {
        var prompt = GeminiBrain.BuildSystemPrompt(Ctx(System.Array.Empty<string>()));
        Assert.Contains("R1 PROMPT: describe your morning in 5 words", prompt);
        Assert.Contains("Alex: snoozed alarm, then coffee", prompt);
    }

    [Fact]
    public void IncludesStyleSummariesWhenProfilesExist()
    {
        var summaries = new[] { "Alex: {\"capitalization\":\"lowercase\"}", "Sam: {\"slang\":[\"ngl\"]}" };
        var prompt = GeminiBrain.BuildSystemPrompt(Ctx(summaries));
        Assert.Contains("THE PEOPLE YOU ARE IMITATING", prompt);
        Assert.Contains("Alex: {\"capitalization\":\"lowercase\"}", prompt);
    }

    [Fact]
    public void OmitsStyleSectionWhenNoProfiles()
    {
        var prompt = GeminiBrain.BuildSystemPrompt(Ctx(System.Array.Empty<string>()));
        Assert.DoesNotContain("THE PEOPLE YOU ARE IMITATING", prompt);
        Assert.DoesNotContain("{{styleSummaries}}", prompt);
        // The section that follows must still be intact.
        Assert.Contains("Everything everyone has said so far:", prompt);
    }

    [Fact]
    public void FillsSelfNameAndPlayerCount()
    {
        var prompt = GeminiBrain.BuildSystemPrompt(Ctx(System.Array.Empty<string>()));
        Assert.Contains("Your name in this game is Riley", prompt);
        Assert.Contains("party game with 2 real people", prompt);
        Assert.DoesNotContain("{{", prompt);
    }

    // Phase 19: a rendered GROUP NOTES block (crew game) is injected on NORMAL/HARD.
    [Fact]
    public void InjectsGroupNotes_OnHard_WhenPresent()
    {
        var notes = GroupProfiler.RenderNotes(
            "{\"vibe\":\"chaotic and fast\",\"groupSlang\":[\"fr\",\"bruh\"],\"detectionHabits\":\"accuse fast\"}");
        Assert.NotNull(notes);

        var ctx = Ctx(System.Array.Empty<string>()) with { Difficulty = "hard", GroupNotes = notes };
        var prompt = GeminiBrain.BuildSystemPrompt(ctx);
        Assert.Contains("GROUP NOTES", prompt);
        Assert.Contains("accuse fast", prompt);
    }

    // EASY never gets the group notes even if they're set (its whole prompt is swapped).
    [Fact]
    public void OmitsGroupNotes_OnEasy_EvenWhenPresent()
    {
        var notes = GroupProfiler.RenderNotes("{\"vibe\":\"chaotic and fast\"}");
        var ctx = Ctx(System.Array.Empty<string>()) with { Difficulty = "easy", GroupNotes = notes };
        var prompt = GeminiBrain.BuildSystemPrompt(ctx);
        Assert.DoesNotContain("GROUP NOTES", prompt);
    }
}
