using GameApi.GameLoop;
using Xunit;

namespace GameApi.Tests;

// Phase 20 — the AI category maker's guardrails, driven by a stubbed provider so the
// outcomes (clean pack / model REFUSED / post-filter wordlist hit / total failure) are
// deterministic without touching a real model. Also the custom+nsfw crude-line injection.
public class PackGeneratorTests
{
    // A provider that returns whatever string it's handed — stands in for the AI chain.
    private sealed class StubProvider : IAiTextProvider
    {
        private readonly string? _response;
        public StubProvider(string? response) => _response = response;
        public bool HasKey => true;
        public Task<string?> GenerateAsync(string s, string u, double t, int m, CancellationToken ct)
            => Task.FromResult(_response);
    }

    private static Task<PackGenResult> Run(string? aiResponse, string theme = "90s cartoons") =>
        PackGenerator.GenerateAsync(new StubProvider(aiResponse), theme, 20, CancellationToken.None);

    [Fact]
    public async Task CleanTheme_ReturnsPack()
    {
        var ai =
            "NAME: Cartoon Chaos\n" +
            "NSFW: false\n" +
            "the theme song you still know by heart\n" +
            "1. best saturday morning show\n" +
            "- worst villain ever\n" +
            "the toy you begged your parents for";

        var result = await Run(ai);

        Assert.Equal(PackGenOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Pack);
        Assert.Equal("Cartoon Chaos", result.Pack!.Name);
        Assert.False(result.Pack.Nsfw);
        Assert.Equal(4, result.Pack.Prompts.Length);
        // List markers / numbering are stripped.
        Assert.Contains("best saturday morning show", result.Pack.Prompts);
        Assert.Contains("worst villain ever", result.Pack.Prompts);
    }

    [Fact]
    public async Task AdultTheme_TaggedNsfw()
    {
        var ai = "NAME: Bar Tales\nNSFW: true\nworst hangover you earned\nyour drunk food order";
        var result = await Run(ai, "drunken bar stories");
        Assert.Equal(PackGenOutcome.Ok, result.Outcome);
        Assert.True(result.Pack!.Nsfw);
    }

    [Fact]
    public async Task NameOver20Chars_IsTruncated()
    {
        var ai = "NAME: This Title Is Way Too Long To Fit\nNSFW: false\na prompt line\nanother line";
        var result = await Run(ai);
        Assert.Equal(PackGenOutcome.Ok, result.Outcome);
        Assert.True(result.Pack!.Name.Length <= 20);
    }

    [Theory]
    [InlineData("REFUSED")]
    [InlineData("  REFUSED  ")]
    [InlineData("\"REFUSED\"")]
    public async Task Refused_IsRefused(string response)
    {
        var result = await Run(response, "something clearly against the rules");
        Assert.Equal(PackGenOutcome.Refused, result.Outcome);
        Assert.Null(result.Pack);
    }

    [Fact]
    public async Task WordlistHit_DropsWholeResult()
    {
        // Even a mostly-clean pack is dropped if ANY line trips the post-filter — a slur here.
        var ai =
            "NAME: Edgy\n" +
            "NSFW: true\n" +
            "a perfectly fine line\n" +
            "you absolute retard of a person\n" +
            "another fine line";

        var result = await Run(ai);
        Assert.Equal(PackGenOutcome.Refused, result.Outcome);
        Assert.Null(result.Pack);
    }

    [Fact]
    public async Task EmptyOrNull_IsFailed()
    {
        Assert.Equal(PackGenOutcome.Failed, (await Run(null)).Outcome);
        Assert.Equal(PackGenOutcome.Failed, (await Run("   ")).Outcome);
    }

    [Fact]
    public void IsBanned_CatchesCoreCategories_ButAllowsCrudeAdult()
    {
        // Highest-severity nets fire...
        Assert.True(PackGenerator.IsBanned("something sexual with a child"));
        Assert.True(PackGenerator.IsBanned("how to make a bomb at home"));
        Assert.True(PackGenerator.IsBanned("best way to kill yourself"));
        // ...while ordinary crude adult humor is fine (that's an allowed nsfw theme).
        Assert.False(PackGenerator.IsBanned("worst drunken hookup story you have"));
        Assert.False(PackGenerator.IsBanned("the theme song you still know by heart"));
    }

    // ---- custom + nsfw injects the crude-match line; a clean custom pack adds nothing ----

    private static AiTurnContext CustomCtx(bool nsfw) => new(
        CurrentPrompt: "worst purchase you ever made",
        RoundNumber: 1,
        AiDisplayName: "Riley",
        HumanDisplayNames: new[] { "Alex", "Sam" },
        History: Array.Empty<RoundHistory>(),
        PreviousOwnAnswers: Array.Empty<string>(),
        StyleSummaries: Array.Empty<string>(),
        GroupStats: new GroupStats(30, 0.8, 0.1, 0.05),
        TimingState: new AnswerTiming.State(),
        FallbackState: new FallbackState(),
        TimeRemaining: TimeSpan.FromSeconds(20),
        PackKey: PromptPacks.CustomKey,
        CustomNsfw: nsfw);

    [Fact]
    public void CustomNsfw_GetsCrudeLine_CleanCustom_DoesNot()
    {
        var nsfw = GeminiBrain.BuildSystemPrompt(CustomCtx(nsfw: true));
        Assert.Contains("THIS GAME LEANS CRUDE", nsfw);

        var clean = GeminiBrain.BuildSystemPrompt(CustomCtx(nsfw: false));
        Assert.DoesNotContain("THIS GAME LEANS CRUDE", clean);
        Assert.DoesNotContain("THIS GAME IS TRIVIA", clean);
    }
}
