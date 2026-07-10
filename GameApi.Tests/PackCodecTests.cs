using GameApi.GameLoop;
using Xunit;

namespace GameApi.Tests;

// Phase 20 — signed share-codes. The signature (HMAC over the gzipped payload, keyed off
// JWT_KEY) is what stops a hand-crafted code from smuggling content past the generation-
// time safety filter, so tamper + oversize rejection are the load-bearing cases.
public class PackCodecTests
{
    private const string Key = "test-only-signing-key-that-is-at-least-64-characters-long-000000000";
    private static PackCodec Codec() => new(Key);

    [Fact]
    public void Encode_Then_Decode_RoundTrips()
    {
        var pack = new CustomPack("90s Cartoons", Nsfw: false,
            new[] { "the theme song you still know by heart", "best saturday morning show", "worst villain ever" });

        var codec = Codec();
        var code = codec.Encode(pack);

        Assert.StartsWith("TTOC1.", code);
        var decoded = codec.TryDecode(code, out var err);
        Assert.NotNull(decoded);
        Assert.Equal("", err);
        Assert.Equal(pack.Name, decoded!.Name);
        Assert.Equal(pack.Nsfw, decoded.Nsfw);
        Assert.Equal(pack.Prompts, decoded.Prompts);
    }

    [Fact]
    public void Nsfw_Flag_SurvivesRoundTrip()
    {
        var pack = new CustomPack("Bar Crawl", Nsfw: true, new[] { "worst hangover you earned" });
        var codec = Codec();
        var decoded = codec.TryDecode(codec.Encode(pack), out _);
        Assert.True(decoded!.Nsfw);
    }

    [Fact]
    public void TamperedBody_IsRejected()
    {
        var codec = Codec();
        var code = codec.Encode(new CustomPack("Clean", false, new[] { "one", "two", "three" }));

        // Flip a character in the body segment (between the two dots).
        var parts = code.Split('.');
        var body = parts[1].ToCharArray();
        body[0] = body[0] == 'A' ? 'B' : 'A';
        var tampered = parts[0] + "." + new string(body) + "." + parts[2];

        var decoded = codec.TryDecode(tampered, out var err);
        Assert.Null(decoded);
        Assert.Contains("messed with", err);
    }

    [Fact]
    public void ForeignKey_CannotForge_AnotherKeysCode()
    {
        // A code signed with a different key must not verify under ours.
        var theirs = new PackCodec("a-totally-different-signing-key-also-64-characters-long-1111111111");
        var code = theirs.Encode(new CustomPack("Sneaky", false, new[] { "smuggled prompt" }));

        var decoded = Codec().TryDecode(code, out var err);
        Assert.Null(decoded);
        Assert.Contains("messed with", err);
    }

    [Fact]
    public void Oversize_IsRejected()
    {
        var huge = "TTOC1." + new string('A', PackCodec.MaxCodeLength + 100);
        var decoded = Codec().TryDecode(huge, out var err);
        Assert.Null(decoded);
        Assert.Contains("too big", err);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello there")]
    [InlineData("TTOC2.abc.def")]
    public void JunkOrWrongPrefix_IsRejected(string code)
    {
        Assert.Null(Codec().TryDecode(code, out var err));
        Assert.NotEqual("", err);
    }
}
