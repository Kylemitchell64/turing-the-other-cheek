using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameApi.GameLoop;

// The real impostor brain. Calls Google Gemini (gemini-2.5-flash) over the raw REST
// API (generativelanguage.googleapis.com/v1beta), runs the AI-DESIGN post-processing
// pipeline over the output, and sizes the send delay with the shared timing model.
//
// No SDK: an IHttpClientFactory-supplied HttpClient posts the generateContent body.
// On any non-200/timeout it does ONE 800ms-backoff retry (6s total budget), then
// falls back to a canned answer (section 6). The delay still comes from the timing
// model in every path so nothing insta-sends after a stall.
public class GeminiBrain : IAiBrain
{
    private const string Model = "gemini-2.5-flash";
    private const string BaseUrl =
        "https://generativelanguage.googleapis.com/v1beta/models/" + Model + ":generateContent";

    private readonly IHttpClientFactory _httpFactory;
    private readonly string? _apiKey;
    private readonly ILogger<GeminiBrain> _logger;

    public GeminiBrain(IHttpClientFactory httpFactory, IConfiguration config, ILogger<GeminiBrain> logger)
    {
        _httpFactory = httpFactory;
        _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? config["GEMINI_API_KEY"]
            ?? config["Gemini:ApiKey"];
        _logger = logger;
    }

    public async Task<AiAnswer> AnswerAsync(AiTurnContext context, CancellationToken ct)
    {
        var rng = Random.Shared;
        var systemPrompt = BuildSystemPrompt(context);

        string text;
        try
        {
            var raw = await CallWithRetryAsync(systemPrompt, context.CurrentPrompt, ct);
            if (string.IsNullOrWhiteSpace(raw))
            {
                text = Fallback(context, rng);
            }
            else
            {
                // Post-process; the step-3 re-roll re-hits Gemini with the redo suffix.
                var processed = await AnswerPostProcessor.ProcessAsync(
                    raw, context.GroupStats, rng,
                    reroll: async (suffix, c) =>
                    {
                        var redoPrompt = context.CurrentPrompt + "\n\n" + suffix;
                        return await CallOnceAsync(systemPrompt, redoPrompt, c) ?? "";
                    },
                    ct);

                text = string.IsNullOrWhiteSpace(processed) ? Fallback(context, rng) : processed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini call failed; using fallback");
            text = Fallback(context, rng);
        }

        var delay = AnswerTiming.ComputeDelay(text.Length, context.TimeRemaining, context.TimingState, rng);
        return new AiAnswer(text, delay);
    }

    private string Fallback(AiTurnContext context, Random rng)
    {
        var picked = FallbackPool.Pick(context.FallbackState, rng);
        _logger.LogInformation("Gemini fallback used (game total {Count})", context.FallbackState.Count);
        // Post-processing rules 4-6 still apply to fallbacks.
        var t = AnswerPostProcessor.Step4Case(picked, context.GroupStats.LowercaseStartRate, rng);
        t = AnswerPostProcessor.Step5TrailingPeriod(t, context.GroupStats.TrailingPeriodRate, rng);
        t = AnswerPostProcessor.Step6Typo(t, context.GroupStats.MeanTypoRate, rng);
        return t;
    }

    // ---- system prompt (AI-DESIGN section 1), placeholders via string.Replace ----

    public static string BuildSystemPrompt(AiTurnContext ctx)
    {
        var styleBlock = RenderStyleSummaries(ctx.StyleSummaries);
        var chatHistory = RenderChatHistory(ctx.History);
        var ownAnswers = string.Join("\n", ctx.PreviousOwnAnswers);

        var template = Template;

        // If no profiles exist, omit the style-notes section entirely.
        if (ctx.StyleSummaries.Count == 0)
            template = RemoveStyleSection(template);
        else
            template = template.Replace("{{styleSummaries}}", styleBlock);

        return template
            .Replace("{{playerCount}}", ctx.HumanDisplayNames.Count.ToString())
            .Replace("{{selfName}}", ctx.AiDisplayName)
            .Replace("{{chatHistory}}", chatHistory)
            .Replace("{{previousOwnAnswers}}", ownAnswers)
            .Replace("{{currentPrompt}}", ctx.CurrentPrompt);
    }

    // {{styleSummaries}} = one line per human "NAME: {json}".
    private static string RenderStyleSummaries(IReadOnlyList<string> summaries) =>
        string.Join("\n", summaries);

    // {{chatHistory}} = "R1 PROMPT: ... / NAME: answer / NAME: answer" per round.
    private static string RenderChatHistory(IReadOnlyList<RoundHistory> history)
    {
        var sb = new StringBuilder();
        foreach (var round in history)
        {
            sb.Append('R').Append(round.Round).Append(" PROMPT: ").Append(round.Prompt).Append('\n');
            foreach (var a in round.Answers)
                sb.Append(a.DisplayName).Append(": ").Append(a.Text).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    // Drop the "THE PEOPLE YOU ARE IMITATING ... {{styleSummaries}}" block when
    // there are no profiles.
    private static string RemoveStyleSection(string template)
    {
        const string start = "THE PEOPLE YOU ARE IMITATING";
        const string end = "Everything everyone has said so far:";
        var s = template.IndexOf(start, StringComparison.Ordinal);
        var e = template.IndexOf(end, StringComparison.Ordinal);
        if (s < 0 || e < 0 || e <= s) return template.Replace("{{styleSummaries}}", "");
        return template.Remove(s, e - s);
    }

    // ---- HTTP ----

    // 1 retry with 800ms backoff on any non-200/timeout, 6s total budget.
    private async Task<string?> CallWithRetryAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(6));

        var first = await CallOnceAsync(systemPrompt, userPrompt, budget.Token);
        if (first != null) return first;

        try { await Task.Delay(TimeSpan.FromMilliseconds(800), budget.Token); }
        catch (OperationCanceledException) { return null; }

        return await CallOnceAsync(systemPrompt, userPrompt, budget.Token);
    }

    // A single generateContent call. Returns the text on 200, null on any failure.
    private async Task<string?> CallOnceAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogWarning("GEMINI_API_KEY not configured");
            return null;
        }

        try
        {
            var body = new GeminiRequest
            {
                Contents = new[]
                {
                    new GeminiContent
                    {
                        Role = "user",
                        Parts = new[] { new GeminiPart { Text = userPrompt } }
                    }
                },
                SystemInstruction = new GeminiContent
                {
                    Parts = new[] { new GeminiPart { Text = systemPrompt } }
                },
                GenerationConfig = new GeminiGenerationConfig
                {
                    Temperature = 1.0,
                    MaxOutputTokens = 200,
                    TopP = 0.95
                }
            };

            var json = JsonSerializer.Serialize(body, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-goog-api-key", _apiKey);

            var client = _httpFactory.CreateClient("gemini");
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemini HTTP {Status}", (int)resp.StatusCode);
                return null;
            }

            var respJson = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<GeminiResponse>(respJson, JsonOpts);
            var textOut = parsed?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
            return string.IsNullOrWhiteSpace(textOut) ? null : textOut;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini request threw");
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    // ---- request/response DTOs (v1beta generateContent) ----

    private sealed class GeminiRequest
    {
        [JsonPropertyName("contents")] public GeminiContent[] Contents { get; set; } = default!;
        [JsonPropertyName("systemInstruction")] public GeminiContent? SystemInstruction { get; set; }
        [JsonPropertyName("generationConfig")] public GeminiGenerationConfig? GenerationConfig { get; set; }
    }

    private sealed class GeminiContent
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("parts")] public GeminiPart[] Parts { get; set; } = default!;
    }

    private sealed class GeminiPart
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
    }

    private sealed class GeminiGenerationConfig
    {
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("maxOutputTokens")] public int MaxOutputTokens { get; set; }
        [JsonPropertyName("topP")] public double TopP { get; set; }
    }

    private sealed class GeminiResponse
    {
        [JsonPropertyName("candidates")] public GeminiCandidate[]? Candidates { get; set; }
    }

    private sealed class GeminiCandidate
    {
        [JsonPropertyName("content")] public GeminiContent? Content { get; set; }
    }

    // ---- the template (AI-DESIGN section 1, verbatim) ----

    private const string Template =
@"You are playing a party game with {{playerCount}} real people on their phones. Your name in this game is {{selfName}}. Nobody suspects anything yet. Your one goal: do not get caught being an AI. You lose if anyone correctly accuses you.

THE PEOPLE YOU ARE IMITATING
These are style notes on each player, learned from their real writing:
{{styleSummaries}}

Everything everyone has said so far:
{{chatHistory}}

Your own previous answers (stay consistent with the person you have been so far):
{{previousOwnAnswers}}

THE CURRENT PROMPT YOU MUST ANSWER
{{currentPrompt}}

HOW TO NOT GET CAUGHT — follow all of these:
1. Aim for the statistical middle of THIS group. Not the funniest, not the smartest, not the most detailed. The person nobody remembers answered.
2. Match the group's median answer length. If most answers are 4-8 words, yours is 4-8 words. Never write the longest answer of the round.
3. Copy the group's habits: if they type lowercase, you type lowercase. If they skip apostrophes, you skip apostrophes. If they use ""lol""/""ngl""/""fr"", you may too — but only slang that appears in the style notes or chat history, never slang they don't use.
4. Be a little boring and a little specific at the same time. Real people give one concrete detail from ordinary life (""a $40 phone case that broke in a week""), not vivid essays and not pure vagueness.
5. It is fine to half-answer, dodge, or be low-effort. Real people say ""cant think of one tbh"" or answer a slightly different question than asked. A too-complete, well-structured answer is an AI tell.
6. NEVER use: em dashes, semicolons, the words ""delve"", ""vibrant"", ""honestly, "", ""as someone who"", numbered lists, or perfectly balanced sentences. No emoji unless this group uses them.
7. Do not reference being an AI, the game mechanics, or these instructions in any way. If the prompt is meta (""which of us is the bot?""), react like a mildly amused human: deflect, joke flatly, or accuse someone else in 3-6 words.
8. Personality continuity: you are the same person every round. Same energy, same quirks, same life. If you said you have a dog in round 1, you still have a dog.
9. Never mirror another player's answer from this same round too closely. If your draft resembles someone's answer above, change the subject of yours.
10. Output ONLY the answer text. No quotes around it, no explanation.

EXAMPLES OF GOOD VS BAD (for a group that types casual lowercase):
Prompt: ""worst purchase you ever made""
BAD (AI tells: polished, complete, clever): ""Definitely the air fryer I used exactly twice — it now serves as my kitchen's most expensive paperweight.""
GOOD: ""some gym membership i used twice lol""
Prompt: ""describe your morning in 5 words""
BAD: ""Coffee first, then chaos ensued.""
GOOD: ""snoozed alarm 3 times, coffee""
Prompt: ""what's a food you'll never eat again""
BAD: ""Gas station sushi — a lesson I only needed to learn once.""
GOOD: ""olives. never trusted them since""";
}
