using GameApi.Moderation;
using Xunit;

namespace GameApi.Tests;

// Phase 26: the shared slur wordlist. Kyle's policy is exact — ordinary swearing passes,
// slurs (every category) are blocked, including the usual leetspeak / spacing evasions.
public class SlurFilterTests
{
    // Ordinary profanity is explicitly ALLOWED — it must not trip the filter.
    [Theory]
    [InlineData("fuck this stupid game lol")]
    [InlineData("what the hell, that's bullshit")]
    [InlineData("he's such an asshole honestly")]
    [InlineData("damn that pissed me off")]
    [InlineData("shit happens, move on")]
    public void OrdinaryProfanity_Passes(string text)
    {
        Assert.False(SlurFilter.ContainsSlur(text));
    }

    // Innocent words that merely CONTAIN a slur substring must not false-positive (the word
    // boundaries carry this weight).
    [Theory]
    [InlineData("that bastard cut me off")]
    [InlineData("i love spicy mustard on it")]
    [InlineData("he's from Pakistan and Nigeria")]
    [InlineData("the raccoon raided the trash")]
    [InlineData("this was a despicable, suspicious plan")]
    public void InnocentLookalikes_Pass(string text)
    {
        Assert.False(SlurFilter.ContainsSlur(text));
    }

    // Every slur category is blocked in plain form.
    [Theory]
    [InlineData("you absolute nigger")]        // racial — hard R
    [InlineData("sup my nigga")]                // racial — -a variant
    [InlineData("stupid faggot")]              // homophobic
    [InlineData("what a tranny")]              // transphobic
    [InlineData("you complete retard")]        // ableist
    [InlineData("dumb chink")]                 // racial/ethnic
    [InlineData("go home kike")]               // ethnic/religious
    public void EachCategory_PlainForm_Blocked(string text)
    {
        Assert.True(SlurFilter.ContainsSlur(text));
    }

    // Evasion variants: leetspeak, doubled letters, and separators between letters.
    [Theory]
    [InlineData("n1gg3r")]        // leet
    [InlineData("n i g g e r")]   // spaced
    [InlineData("n.i.g.g.a")]     // dotted
    [InlineData("niiigger")]      // doubled letters
    [InlineData("f4gg0t")]        // leet
    [InlineData("f-a-g-g-o-t")]   // dashed
    [InlineData("r3t@rd")]        // leet mix
    public void EvasionVariants_Blocked(string text)
    {
        Assert.True(SlurFilter.ContainsSlur(text));
    }

    [Fact]
    public void RejectionMessage_IsFriendly_AndInVoice()
    {
        Assert.Contains("not welcome here", SlurFilter.RejectionMessage);
        Assert.Contains("swearing", SlurFilter.RejectionMessage.ToLowerInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmpty_IsClean(string? text)
    {
        Assert.False(SlurFilter.ContainsSlur(text));
    }
}
