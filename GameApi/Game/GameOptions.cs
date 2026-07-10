namespace GameApi.GameLoop;

// Host-picked lobby options beyond the prompt pack: how sneaky the impostor is and
// how long everyone gets to answer. Both live on the Lobby (in-memory, never in the
// DB) and ride the same SetLobbyOptions/LobbyOptionsChanged plumbing as the pack.

// The three impostor personas. Same model underneath — the difficulty is which parts
// of the disguise kit are switched on. HARD is the full AI-DESIGN treatment (what
// shipped originally). NORMAL leaves seams. EASY is deliberately catchable.
public sealed record DifficultyProfile(
    string Key,
    // Inject per-human style summaries into the system prompt.
    bool UseStyleSummaries,
    // Drop the sharpest countermeasure rules from the prompt (the statistical-middle
    // rule, the low-effort-answers rule, the same-round-similarity rule).
    bool TrimSharpRules,
    // Swap the whole disguise prompt for a short "polite party guest" persona — the
    // default AI voice bleeds straight through. Easy only.
    bool EasyPersona,
    // Post-processing: inject typos / conform case+trailing-period to the group.
    bool InjectTypos,
    bool ConformToGroup,
    // Timing: allow the last-seconds deadline scrape branch.
    bool AllowDeadlineScrape,
    // Timing: instead of the human-ish gaussian, submit in a tight fixed band every
    // round (fraction of the answer window). Suspicious consistency IS the tell.
    (double MinFrac, double MaxFrac)? FixedTimingBand)
{
    public const string DefaultKey = "normal";

    public static readonly DifficultyProfile Easy = new(
        "easy", UseStyleSummaries: false, TrimSharpRules: false, EasyPersona: true,
        InjectTypos: false, ConformToGroup: false, AllowDeadlineScrape: false,
        FixedTimingBand: (0.55, 0.8));

    public static readonly DifficultyProfile Normal = new(
        "normal", UseStyleSummaries: true, TrimSharpRules: true, EasyPersona: false,
        InjectTypos: false, ConformToGroup: true, AllowDeadlineScrape: false,
        FixedTimingBand: null);

    public static readonly DifficultyProfile Hard = new(
        "hard", UseStyleSummaries: true, TrimSharpRules: false, EasyPersona: false,
        InjectTypos: true, ConformToGroup: true, AllowDeadlineScrape: true,
        FixedTimingBand: null);

    public static bool IsValidKey(string key) => key is "easy" or "normal" or "hard";

    public static DifficultyProfile Get(string key) => key switch
    {
        "easy" => Easy,
        "hard" => Hard,
        _ => Normal,
    };
}

// Game modes (phase 22). CLASSIC is the original hidden-impostor game. REVERSE flips it:
// there is no impostor — everyone answers, and at each reveal the AI publicly guesses which
// player wrote which (shuffled, anonymous) answer. Host-picked pre-start via SetLobbyOptions,
// kept across a rematch like the other options.
public static class GameModes
{
    public const string Classic = "classic";
    public const string Reverse = "reverse";
    public const string DefaultKey = Classic;

    public static bool IsValidKey(string? key) => key is Classic or Reverse;
    public static bool IsReverse(string? key) =>
        string.Equals(key, Reverse, StringComparison.Ordinal);
}

// Answer-window presets the host can pick. Only the Prompting window changes —
// reveal/accuse/veto windows stay at their spec durations.
public static class PaceOptions
{
    public const string DefaultKey = "standard";

    private static readonly Dictionary<string, int> Seconds = new()
    {
        ["flash"] = 10,
        ["quick"] = 20,
        ["standard"] = 30,
        ["relaxed"] = 45,
        ["snail"] = 60,
    };

    public static bool IsValidKey(string key) => Seconds.ContainsKey(key);

    public static int WindowSeconds(string key) =>
        Seconds.TryGetValue(key, out var s) ? s : Seconds[DefaultKey];

    // Short windows mean short answers — nobody types a paragraph in 10 seconds.
    public static bool IsShortWindow(string key) => key is "flash" or "quick";
}
