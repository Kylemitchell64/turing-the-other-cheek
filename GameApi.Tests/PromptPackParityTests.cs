using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GameApi.GameLoop;
using Xunit;

namespace GameApi.Tests;

// Cross-language parity (mirrors Phase 12's CharacterHashTests): the host pack selector
// on the client renders game-client/src/game/packs.js in array order, while the server
// draws prompts from PromptPacks.All. If those two key lists ever drift — different keys
// or a different order — the selector would show packs the server can't serve, or in the
// wrong order. This test reads the real packs.js and fails the moment they disagree, so
// the SFW-first / 18+-last ordering can't silently rot.
public class PromptPackParityTests
{
    [Fact]
    public void ClientPacksJs_KeyOrder_MatchesServerAll()
    {
        var serverKeys = PromptPacks.All.Select(p => p.Key).ToArray();
        var clientKeys = ReadClientPackKeys();

        Assert.Equal(serverKeys, clientKeys);
    }

    // Pull the `key: "..."` values out of the PACKS array in packs.js, in file order.
    private static string[] ReadClientPackKeys()
    {
        var js = File.ReadAllText(PacksJsPath());
        return Regex.Matches(js, "key:\\s*\"(?<k>[a-z0-9_]+)\"")
            .Select(m => m.Groups["k"].Value)
            .ToArray();
    }

    // Locate packs.js relative to this source file so the test works from any bin dir.
    private static string PacksJsPath([CallerFilePath] string thisFile = "")
    {
        var testsDir = Path.GetDirectoryName(thisFile)!;      // .../GameApi.Tests
        var repoRoot = Path.GetDirectoryName(testsDir)!;      // repo root
        var path = Path.Combine(repoRoot, "game-client", "src", "game", "packs.js");
        Assert.True(File.Exists(path), $"packs.js not found at {path}");
        return path;
    }
}
