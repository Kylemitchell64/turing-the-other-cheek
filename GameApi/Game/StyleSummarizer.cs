using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GameApi.Data;
using GameApi.Models;

namespace GameApi.GameLoop;

// AI-DESIGN section 3: the per-user style summarizer. Reads a user's writing-sample
// pool (uploads + harvested game answers), truncates to the most recent 6000 chars,
// and asks Gemini for a compact JSON style profile at temperature 0.2. The response is
// parsed with a strict extractor (first '{' .. last '}'); on parse failure it retries
// once with a "Return ONLY the JSON object." nudge, then gives up and keeps the old
// profile. Under the min-sample gate (< 80 chars) it skips the API entirely and stores
// the defaults JSON. Result lands in StyleProfiles.SummaryJson (jsonb).
public class StyleSummarizer
{
    private const int SampleBudgetChars = 6000;
    private const int MinSampleChars = 80;

    // The defaults JSON, used under the min-sample gate and as the shape reference.
    // Matches the summarizer prompt's "if samples are too thin" defaults.
    public const string DefaultsJson =
        "{\"avgLength\":45,\"capitalization\":\"mixed\",\"punctuationQuirks\":\"\",\"typoRate\":0.08,\"slang\":[],\"sentenceStyle\":\"casual, short\",\"topics\":[]}";

    // The failover chain (any of Gemini/Groq/Cerebras answers). GeminiClient still
    // satisfies this interface directly, so the existing unit tests pass one in unchanged.
    private readonly IAiTextProvider _gemini;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StyleSummarizer> _logger;

    public StyleSummarizer(
        IAiTextProvider gemini,
        IServiceScopeFactory scopeFactory,
        ILogger<StyleSummarizer> logger)
    {
        _gemini = gemini;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // Regenerate (or create) the profiles for a set of users, but only where a profile
    // is missing or stale (older than the user's newest sample). Fire-and-forget safe:
    // catches everything so a DB/API hiccup never crashes the caller. Used both after a
    // game (harvested answers just landed) and on-demand at lobby start.
    public async Task RefreshStaleProfilesAsync(IEnumerable<string> userIds, CancellationToken ct = default)
    {
        foreach (var userId in userIds.Distinct())
        {
            try { await RefreshIfStaleAsync(userId, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Style profile refresh failed for user {UserId}", userId);
            }
        }
    }

    private async Task RefreshIfStaleAsync(string userId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();

        var samples = await db.WritingSamples
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new { s.Text, s.CreatedAt })
            .ToListAsync(ct);

        if (samples.Count == 0) return;

        var newestSample = samples.Max(s => s.CreatedAt);
        var profile = await db.StyleProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);

        // Not stale: a profile exists and is at least as new as the newest sample.
        if (profile != null && profile.SummaryJson != null && profile.UpdatedAt >= newestSample)
            return;

        // Most recent 6000 chars of the pool.
        var pool = string.Join("\n", samples.Select(s => s.Text));
        if (pool.Length > SampleBudgetChars)
            pool = pool[^SampleBudgetChars..];

        var summaryJson = await SummarizeAsync(pool, profile?.SummaryJson, ct);
        if (summaryJson == null) return; // API failed on a real pool → keep the old profile.

        if (profile == null)
        {
            profile = new StyleProfile { UserId = userId };
            db.StyleProfiles.Add(profile);
        }
        profile.SummaryJson = summaryJson;
        profile.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // Produce the style JSON for a pool. Returns null only when a real API attempt
    // failed to parse (so the caller keeps the previous profile). The min-sample gate
    // returns the defaults JSON without ever calling the API.
    public async Task<string?> SummarizeAsync(string pool, string? previousJson, CancellationToken ct)
    {
        // Min-sample gate: too thin → defaults JSON, no API call.
        if (pool.Trim().Length < MinSampleChars)
            return DefaultsJson;

        if (!_gemini.HasKey)
        {
            _logger.LogWarning("Style summarizer has no GEMINI_API_KEY; storing defaults");
            return previousJson ?? DefaultsJson;
        }

        var userPrompt = Prompt.Replace("{{samples}}", pool);

        var raw = await _gemini.GenerateAsync(
            systemPrompt: "", userPrompt, temperature: 0.2, maxOutputTokens: 400, ct);
        var parsed = ExtractJson(raw);
        if (parsed != null) return parsed;

        // One retry with the strict-JSON nudge appended.
        var retry = await _gemini.GenerateAsync(
            systemPrompt: "",
            userPrompt + "\nReturn ONLY the JSON object.",
            temperature: 0.2, maxOutputTokens: 400, ct);
        parsed = ExtractJson(retry);
        if (parsed != null) return parsed;

        _logger.LogWarning("Style summarizer failed to parse Gemini JSON; keeping old profile");
        return null; // keep the old profile
    }

    // Strict JSON extractor: take the substring from the first '{' to the last '}' and
    // verify it parses. Returns the compacted JSON, or null if there's nothing valid.
    public static string? ExtractJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        var slice = raw.Substring(start, end - start + 1);
        try
        {
            using var doc = JsonDocument.Parse(slice);
            // Re-serialize so we store compact, canonical JSON in the jsonb column.
            return JsonSerializer.Serialize(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // AI-DESIGN section 3, verbatim.
    private const string Prompt =
@"Analyze how this specific person writes in casual chat. Writing samples:
<<<
{{samples}}
>>>
Return ONLY a JSON object, no markdown, exactly this shape:
{""avgLength"": <int, average answer length in characters, 10-200>,
 ""capitalization"": <""lowercase""|""sentence""|""mixed"">,
 ""punctuationQuirks"": <string, e.g. ""no apostrophes, uses ... a lot, no trailing periods"">,
 ""typoRate"": <float 0-0.3, fraction of messages containing a typo>,
 ""slang"": [<up to 6 strings actually used, e.g. ""lol"",""ngl"">],
 ""sentenceStyle"": <string, one plain sentence, e.g. ""short fragments, starts with 'i' a lot, deadpan"">,
 ""topics"": [<up to 5 recurring interests/topics as short strings>]}
Rules: base every field ONLY on the samples. If samples are too thin for a field, use these defaults: avgLength 45, capitalization ""mixed"", punctuationQuirks """", typoRate 0.08, slang [], sentenceStyle ""casual, short"", topics [].";
}
