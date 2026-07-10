using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace GameApi.Lobbies;

// Process-wide singleton holding every active lobby, keyed by join code.
// Thread-safety: the dictionary itself is concurrent; per-lobby state is guarded
// by each Lobby's Sync lock (taken in the hub).
public class LobbyStore
{
    private readonly ConcurrentDictionary<string, Lobby> _lobbies = new();

    // Unambiguous uppercase set — no O/0/I/1/L so a code read off a phone screen
    // is never misheard.
    private const string CodeChars = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    // ~40 plausible human first names, mixed vibes. StartGame pulls one that
    // doesn't collide with any real player's display name.
    private static readonly string[] AiNames =
    {
        "Jake", "Emily", "Marcus", "Priya", "Tyler", "Sam", "Nora", "Diego",
        "Chloe", "Andre", "Maya", "Ethan", "Leah", "Omar", "Grace", "Liam",
        "Zoe", "Kevin", "Aisha", "Ben", "Ruby", "Carlos", "Hannah", "Jamal",
        "Sofia", "Ryan", "Fatima", "Luke", "Ivy", "Nathan", "Elena", "Cole",
        "Tara", "Malik", "Jenna", "Wyatt", "Lena", "Dev", "Paige", "Isaac"
    };

    public Lobby Create(string hostUserId)
    {
        // Retry until we mint a code not already in use.
        while (true)
        {
            var code = GenerateCode();
            var lobby = new Lobby { Code = code, HostUserId = hostUserId };
            if (_lobbies.TryAdd(code, lobby))
                return lobby;
        }
    }

    public Lobby? Get(string code) =>
        _lobbies.TryGetValue(code, out var lobby) ? lobby : null;

    // Snapshot of all live lobbies. Used to locate the lobby a connection belongs
    // to on disconnect / StartGame.
    public IEnumerable<Lobby> All => _lobbies.Values;

    public bool Remove(string code) => _lobbies.TryRemove(code, out _);

    // Pick a name not equal (case-insensitive) to any joined player's display name.
    public string PickAiName(IEnumerable<string> takenDisplayNames)
    {
        var taken = new HashSet<string>(takenDisplayNames, StringComparer.OrdinalIgnoreCase);
        var candidates = AiNames.Where(n => !taken.Contains(n)).ToList();

        // 40-name pool vs max 8 players — candidates is effectively never empty,
        // but fall back to a numbered name if a lobby somehow used them all.
        if (candidates.Count == 0)
            return "Alex" + RandomNumberGenerator.GetInt32(100, 1000);

        return candidates[RandomNumberGenerator.GetInt32(candidates.Count)];
    }

    // A single 5-char code off the unambiguous alphabet. Public + static so crews reuse
    // the exact same generator for their PERSISTENT codes (uniqueness checked against the
    // DB by the caller, a different namespace from these live lobby codes).
    public static string GenerateCode()
    {
        Span<char> chars = stackalloc char[5];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = CodeChars[RandomNumberGenerator.GetInt32(CodeChars.Length)];
        return new string(chars);
    }
}
