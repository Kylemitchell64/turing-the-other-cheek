using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GameApi.Data;

namespace GameApi.GameLoop;

// Phase 19 — the crew GROUP profiler. The crew-level analogue of StyleSummarizer: after a
// crew game ends, it feeds the just-played transcript (plus the crew's stored GroupProfileJson
// as prior) to the AI chain and gets back a compact JSON of how THIS group plays together —
// its vibe, running jokes, slang, who teases whom, and how it hunts the impostor. Strict JSON
// extraction, one retry, keep-old-on-failure, temperature 0.2 (same discipline as AI-DESIGN §3).
// The rendered notes are injected into the impostor's system prompt at crew-game start.
public class GroupProfiler
{
    private const int TranscriptBudgetChars = 6000;
    private const int MinTranscriptChars = 80;

    private readonly IAiTextProvider _ai;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GroupProfiler> _logger;

    public GroupProfiler(IAiTextProvider ai, IServiceScopeFactory scopeFactory, ILogger<GroupProfiler> logger)
    {
        _ai = ai;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // Fire-and-forget after a crew game: rebuild the crew's group profile from this game's
    // human answers + the stored prior, and persist it. Swallows everything so a DB/API
    // hiccup never crashes the engine loop.
    public async Task UpdateAfterGameAsync(int crewId, IReadOnlyList<string> answerLines, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();

            var crew = await db.Crews.FirstOrDefaultAsync(c => c.Id == crewId, ct);
            if (crew == null) return;

            var transcript = string.Join("\n", answerLines);
            if (transcript.Length > TranscriptBudgetChars)
                transcript = transcript[^TranscriptBudgetChars..];

            var updated = await BuildProfileAsync(transcript, crew.GroupProfileJson, ct);
            if (updated == null) return; // API/parse failure → keep the old profile

            crew.GroupProfileJson = updated;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Group profile updated for crew {CrewId}", crewId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Group profile update failed for crew {CrewId}", crewId);
        }
    }

    // Produce the group JSON for a transcript. Returns null ONLY when a real API attempt
    // failed to parse (so the caller keeps the previous profile). Under the min-transcript
    // gate with no prior, returns null too (nothing worth storing yet).
    public async Task<string?> BuildProfileAsync(string transcript, string? previousJson, CancellationToken ct)
    {
        if (transcript.Trim().Length < MinTranscriptChars && string.IsNullOrWhiteSpace(previousJson))
            return null;

        if (!_ai.HasKey)
        {
            _logger.LogWarning("Group profiler has no AI key; keeping old group profile");
            return null;
        }

        var userPrompt = Prompt
            .Replace("{{prior}}", string.IsNullOrWhiteSpace(previousJson) ? "(none yet)" : previousJson)
            .Replace("{{transcript}}", transcript);

        var raw = await _ai.GenerateAsync(systemPrompt: "", userPrompt, temperature: 0.2, maxOutputTokens: 500, ct);
        var parsed = StyleSummarizer.ExtractJson(raw);
        if (parsed != null) return parsed;

        var retry = await _ai.GenerateAsync(
            systemPrompt: "", userPrompt + "\nReturn ONLY the JSON object.",
            temperature: 0.2, maxOutputTokens: 500, ct);
        parsed = StyleSummarizer.ExtractJson(retry);
        if (parsed != null) return parsed;

        _logger.LogWarning("Group profiler failed to parse JSON; keeping old group profile");
        return null;
    }

    // Render a GroupProfileJson into the GROUP NOTES block injected into the impostor's
    // system prompt. Returns null when there's nothing renderable (null/blank/unparseable),
    // so the caller only injects a real block. Only fields with content emit a line.
    public static string? RenderNotes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
        if (root.ValueKind != JsonValueKind.Object) return null;

        var sb = new StringBuilder();
        sb.Append("GROUP NOTES — you have played with this exact group before. Blend into their habits, but NEVER hint that you know them or that this is a returning group.");

        var vibe = Str(root, "vibe");
        if (vibe.Length > 0) sb.Append("\nGroup vibe: ").Append(vibe);

        var topics = Arr(root, "commonTopics");
        if (topics.Length > 0) sb.Append("\nThey keep talking about: ").Append(string.Join(", ", topics));

        var slang = Arr(root, "groupSlang");
        if (slang.Length > 0) sb.Append("\nSlang this group uses (you may use ONLY these, never invent others): ").Append(string.Join(", ", slang));

        var jokes = Arr(root, "runningJokes");
        if (jokes.Length > 0) sb.Append("\nRunning jokes / callbacks: ").Append(string.Join("; ", jokes));

        var length = Str(root, "answerLengthNorm");
        if (length.Length > 0) sb.Append("\nTypical answer style: ").Append(length);

        var teases = Arr(root, "whoTeasesWhom");
        if (teases.Length > 0) sb.Append("\nWho teases whom: ").Append(string.Join("; ", teases));

        var detection = Str(root, "detectionHabits");
        if (detection.Length > 0)
            sb.Append("\nHow this group hunts the impostor: ").Append(detection)
              .Append(" — do not walk into these tells.");

        return sb.ToString();
    }

    private static string Str(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";

    private static string[] Arr(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => (e.GetString() ?? "").Trim())
            .Where(s => s.Length > 0)
            .ToArray();
    }

    // The group-extraction prompt, in the spirit of AI-DESIGN §3 (strict JSON, defaults on
    // thin evidence). Folds the prior profile forward so knowledge accumulates over games.
    private const string Prompt =
@"Analyze how THIS specific group of friends plays a chat party game together — their shared vibe, inside jokes, slang, and how they try to catch the hidden impostor.

Prior notes on this group (fold these forward; may be empty):
<<<
{{prior}}
>>>

Transcript of their latest game, one line per answer as ""NAME: text"":
<<<
{{transcript}}
>>>

Return ONLY a JSON object, no markdown, exactly this shape:
{""vibe"": <string, one plain sentence on the group's overall energy>,
 ""runningJokes"": [<up to 5 short strings: recurring bits or callbacks this group makes>],
 ""commonTopics"": [<up to 6 short strings: subjects they keep bringing up>],
 ""groupSlang"": [<up to 8 short strings: words/phrases this group actually uses>],
 ""answerLengthNorm"": <string, e.g. ""short lowercase fragments"" or ""full sentences"">,
 ""whoTeasesWhom"": [<up to 5 short strings like ""Sam ribs Alex"">],
 ""detectionHabits"": <string, how this group hunts the AI, e.g. ""accuse fast"", ""bait with meta prompts"", ""watch for over-polished answers"">}
Rules: base every field ONLY on real evidence from the prior notes + transcript. Keep prior facts that still hold and fold in what's new. If a field has no evidence, use an empty array or an empty string.";
}
