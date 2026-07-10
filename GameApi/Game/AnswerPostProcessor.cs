namespace GameApi.GameLoop;

// Section 2 of AI-DESIGN: the deterministic post-processing pipeline applied to
// Gemini's raw output, in order. Steps 1-7. Kept brain-agnostic and side-effect free
// except for the reroll callback (step 3) so it can be unit-tested with a stub brain
// and a seeded Random.
public static class AnswerPostProcessor
{
    // The extra line appended to the prompt for the single banned-substring re-roll.
    public const string RerollSuffix =
        "Your last answer sounded like an AI. Redo it, plainer and shorter.";

    private static readonly string[] BannedSubstrings =
    {
        "—",      // em dash
        "as an AI",
        "I'm just",
        "delve",
        "vibrant"
    };

    private static readonly string[] BannedWords = { "delve", "vibrant" };

    // Run the full pipeline. `reroll` is invoked at most once (step 3) with the
    // appended redo instruction; if it's null we skip straight to the strip fallback.
    // conformToGroup gates steps 4-5 and injectTypos gates step 6 — easy/normal
    // difficulty turn parts of the disguise off on purpose (perfect punctuation and
    // zero typos are intended tells there).
    public static async Task<string> ProcessAsync(
        string raw,
        GroupStats stats,
        Random rng,
        Func<string, CancellationToken, Task<string>>? reroll,
        CancellationToken ct,
        bool conformToGroup = true,
        bool injectTypos = true)
    {
        // 1. Strip wrapping quotes/backticks; first line only if multiline.
        var text = Step1StripAndFirstLine(raw);

        // 2. Hard clamp length.
        text = Step2Clamp(text, stats.MedianAnswerLength);

        // 3. Banned substrings -> one re-roll, then strip if still bad.
        if (ContainsBanned(text))
        {
            string? rerolled = null;
            if (reroll != null)
            {
                try { rerolled = await reroll(RerollSuffix, ct); }
                catch { rerolled = null; }
            }

            if (rerolled != null)
            {
                var candidate = Step2Clamp(Step1StripAndFirstLine(rerolled), stats.MedianAnswerLength);
                text = ContainsBanned(candidate) ? StripBanned(candidate) : candidate;
            }
            else
            {
                text = StripBanned(text);
            }
        }

        // 4. Case conformance.
        if (conformToGroup)
            text = Step4Case(text, stats.LowercaseStartRate, rng);

        // 5. Trailing period.
        if (conformToGroup)
            text = Step5TrailingPeriod(text, stats.TrailingPeriodRate, rng);

        // 6. Typo injection.
        if (injectTypos)
            text = Step6Typo(text, stats.MeanTypoRate, rng);

        // 7. Length floor — caller handles empty by substituting a fallback.
        return text;
    }

    // ---- step 1 ----
    public static string Step1StripAndFirstLine(string raw)
    {
        var text = (raw ?? "").Trim();
        var nl = text.IndexOfAny(new[] { '\n', '\r' });
        if (nl >= 0) text = text[..nl].Trim();

        // Strip a single matching pair of wrapping quotes/backticks.
        text = StripWrappingPair(text, '"');
        text = StripWrappingPair(text, '\'');
        text = StripWrappingPair(text, '`');
        return text.Trim();
    }

    private static string StripWrappingPair(string text, char q)
    {
        if (text.Length >= 2 && text[0] == q && text[^1] == q)
            return text[1..^1];
        return text;
    }

    // ---- step 2 ----
    public static string Step2Clamp(string text, double groupMedian)
    {
        if (text.Length > 240)
        {
            var cut = LastBoundaryUnder(text, 240);
            text = (cut > 0 ? text[..cut] : text[..240]).TrimEnd();
        }

        var hardMax = groupMedian * 2.5;
        if (hardMax > 0 && text.Length > hardMax)
        {
            var limit = (int)hardMax;
            var space = limit < text.Length ? text.LastIndexOf(' ', Math.Min(limit, text.Length - 1)) : -1;
            text = (space > 0 ? text[..space] : text[..Math.Min(limit, text.Length)]).TrimEnd();
        }

        return text;
    }

    // Last sentence/clause boundary at or under `max` chars.
    private static int LastBoundaryUnder(string text, int max)
    {
        var scan = Math.Min(max, text.Length - 1);
        for (var i = scan; i > 0; i--)
        {
            var c = text[i];
            if (c is '.' or '!' or '?' or ',' or ';' or ':')
                return i + 1;
        }
        return -1;
    }

    // ---- step 3 helpers ----
    public static bool ContainsBanned(string text)
    {
        if (text.Contains('—')) return true;
        if (text.Contains(" - ")) return true; // " - " used parenthetically
        foreach (var b in BannedSubstrings)
            if (text.Contains(b, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string StripBanned(string text)
    {
        // em dashes -> ", "
        text = text.Replace("—", ", ");
        text = text.Replace(" - ", ", ");
        // banned words -> removed
        foreach (var w in BannedWords)
            text = ReplaceIgnoreCase(text, w, "");
        // "as an AI" / "I'm just" removed too (they'd otherwise survive the re-roll fail)
        text = ReplaceIgnoreCase(text, "as an AI", "");
        text = ReplaceIgnoreCase(text, "I'm just", "");
        // tidy doubled spaces left behind
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        return text.Trim();
    }

    private static string ReplaceIgnoreCase(string text, string find, string with)
    {
        int idx;
        while ((idx = text.IndexOf(find, StringComparison.OrdinalIgnoreCase)) >= 0)
            text = text.Remove(idx, find.Length).Insert(idx, with);
        return text;
    }

    // ---- step 4 ----
    public static string Step4Case(string text, double lowercaseStartRate, Random rng)
    {
        if (text.Length == 0) return text;
        if (lowercaseStartRate >= 0.6 && char.IsUpper(text[0]))
        {
            // Lowercase with probability = the observed rate.
            if (rng.NextDouble() < lowercaseStartRate)
                text = char.ToLowerInvariant(text[0]) + text[1..];
        }
        return text;
    }

    // ---- step 5 ----
    public static string Step5TrailingPeriod(string text, double trailingPeriodRate, Random rng)
    {
        if (text.Length == 0) return text;
        if (trailingPeriodRate <= 0.3 && text.EndsWith('.') && !text.EndsWith(".."))
        {
            if (rng.NextDouble() < 0.8)
                text = text[..^1];
        }
        return text;
    }

    // ---- step 6 ----
    public static string Step6Typo(string text, double meanTypoRate, Random rng)
    {
        var p = Math.Clamp(meanTypoRate, 0.05, 0.25);
        if (rng.NextDouble() >= p) return text;

        var words = text.Split(' ');
        if (words.Length < 2) return text; // never typo the first word; need a 2nd

        // Try to apply exactly one typo, scanning candidate words (index >= 1).
        var order = Enumerable.Range(1, words.Length - 1).OrderBy(_ => rng.Next()).ToList();
        foreach (var wi in order)
        {
            var applied = TryTypo(words[wi], rng);
            if (applied != null)
            {
                words[wi] = applied;
                return string.Join(' ', words);
            }
        }
        return text;
    }

    // One of: swap two adjacent letters in a word >=5 chars; drop an apostrophe
    // (dont/cant/im); double a letter at word end. Returns null if none applies.
    private static string? TryTypo(string word, Random rng)
    {
        var options = new List<Func<string?>>
        {
            () => SwapAdjacent(word, rng),
            () => DropApostrophe(word),
            () => DoubleEnd(word),
        };
        // random order so we don't always prefer the same typo kind
        foreach (var opt in options.OrderBy(_ => rng.Next()))
        {
            var r = opt();
            if (r != null && r != word) return r;
        }
        return null;
    }

    private static string? SwapAdjacent(string word, Random rng)
    {
        if (word.Length < 5) return null;
        // pick an interior position to swap (keep it inside the alpha run)
        var i = 1 + rng.Next(word.Length - 2);
        var chars = word.ToCharArray();
        (chars[i], chars[i - 1]) = (chars[i - 1], chars[i]);
        return new string(chars);
    }

    private static string? DropApostrophe(string word)
    {
        var idx = word.IndexOf('\'');
        if (idx < 0) return null;
        return word.Remove(idx, 1);
    }

    private static string? DoubleEnd(string word)
    {
        if (word.Length == 0) return null;
        var last = word[^1];
        if (!char.IsLetter(last)) return null;
        return word + last;
    }

    // Used by the engine to build the current game's group stats.
    public static GroupStats ComputeStats(IReadOnlyCollection<string> answers, IReadOnlyCollection<double> typoRates)
    {
        var real = answers.Where(a => !string.IsNullOrWhiteSpace(a) && a != "(no answer)").ToList();
        double median = 45;
        double lowerStart = 0.5;
        double trailingPeriod = 0.5;
        if (real.Count > 0)
        {
            var lengths = real.Select(a => (double)a.Length).OrderBy(x => x).ToList();
            median = lengths[lengths.Count / 2];
            lowerStart = real.Count(a => char.IsLower(a.TrimStart().FirstOrDefault())) / (double)real.Count;
            trailingPeriod = real.Count(a => a.TrimEnd().EndsWith('.')) / (double)real.Count;
        }

        var meanTypo = typoRates.Count > 0 ? typoRates.Average() : 0.08;
        return new GroupStats(median, lowerStart, trailingPeriod, meanTypo);
    }
}
