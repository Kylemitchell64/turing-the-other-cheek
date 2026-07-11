using System.Text;
using System.Text.RegularExpressions;

namespace GameApi.Moderation;

// Kyle's content policy for anything a user types that TRAINS the AI (writing samples) or
// SEEDS generated content (custom-pack themes + titles): ordinary swearing is fine — "fuck",
// "shit", "asshole" all pass untouched. Slurs are not: the n-word (hard-R and the -a variant),
// plus the standard racial / ethnic / homophobic / transphobic / ableist slurs. One shared
// wordlist so the sample-save path and the pack generator agree on exactly what "a slur" is.
//
// Matching is deliberately evasion-aware: leetspeak (0/o, @/a, 1/!/i, 3/e, 5/$/s, 7/t),
// doubled letters (niiigga), and separators between letters (n.i.g.g.a, "n i g g a"). It is
// intentionally blunt at the edges — a rare false positive just asks the user to reword.
public static class SlurFilter
{
    // Shown to the user when a save is rejected — Kyle's app voice.
    public const string RejectionMessage =
        "that word's not welcome here — swearing's fine, slurs aren't.";

    // Base slur roots. Inflections that end in a letter (nigger/nigga, retard/tard) are listed
    // as their own roots so the trailing word-boundary still holds instead of trying to bolt
    // optional suffixes onto one pattern.
    private static readonly string[] Roots =
    {
        // the n-word — hard-R and the -a variant, plus common spellings
        "nigger", "nigga", "niggah", "sandnigger",
        // racial / ethnic / religious
        "chink", "gook", "spic", "wetback", "beaner", "kike", "coon",
        "jigaboo", "raghead", "towelhead", "wop", "dago", "zipperhead",
        // homophobic
        "faggot", "faggy", "fag", "dyke",
        // transphobic
        "tranny", "trannie", "shemale", "ladyboy",
        // ableist
        "retard", "retarded", "tard", "mongoloid", "spastic", "spaz",
    };

    // Leet / lookalike expansions per letter. Anything not listed matches itself.
    private static readonly Dictionary<char, string> Leet = new()
    {
        ['a'] = "a4@",
        ['b'] = "b8",
        ['c'] = "c(",
        ['e'] = "e3",
        ['g'] = "g9",
        ['i'] = "i1!|",
        ['l'] = "l1|",
        ['o'] = "o0",
        ['s'] = "s5$",
        ['t'] = "t7+",
    };

    // Filler tolerated BETWEEN letters (the classic "n.i.g.g.a" / "n i g g a" evasion). Kept
    // short and bounded so the whole thing can never catastrophically backtrack.
    private const string Sep = @"[\s._\-*]{0,2}";

    private static readonly Regex[] Patterns = Roots.Select(Build).ToArray();

    private static Regex Build(string root)
    {
        var sb = new StringBuilder();
        sb.Append("(?<![a-z0-9])"); // left word boundary (no letter/digit immediately before)
        for (int i = 0; i < root.Length; i++)
        {
            var c = char.ToLowerInvariant(root[i]);
            var chars = Leet.TryGetValue(c, out var set) ? set : c.ToString();
            sb.Append('[').Append(Regex.Escape(chars)).Append(']');
            sb.Append("{1,3}"); // doubled-letter tolerance
            if (i < root.Length - 1) sb.Append(Sep);
        }
        sb.Append("(?![a-z0-9])"); // right word boundary
        return new Regex(sb.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    // True if the text contains a slur under any of the tolerated evasion forms.
    public static bool ContainsSlur(string? text) =>
        !string.IsNullOrEmpty(text) && Patterns.Any(r => r.IsMatch(text!));
}
