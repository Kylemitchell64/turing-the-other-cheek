using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameApi.GameLoop;

// Shared Gemini transport. Both the impostor brain and the style summarizer post to
// the same v1beta generateContent endpoint with the raw HttpClient (no SDK), so the
// request/response DTOs, key handling, and a single generateContent call all live here.
// Callers layer their own retry/backoff policy on top (the brain does 1 retry with a
// 6s budget; the summarizer does 1 retry on a parse failure).
public class GeminiClient
{
    private const string Model = "gemini-2.5-flash";
    private const string BaseUrl =
        "https://generativelanguage.googleapis.com/v1beta/models/" + Model + ":generateContent";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GeminiClient> _logger;

    public string? ApiKey { get; }
    public bool HasKey => !string.IsNullOrEmpty(ApiKey);

    public GeminiClient(IHttpClientFactory httpFactory, IConfiguration config, ILogger<GeminiClient> logger)
    {
        _httpFactory = httpFactory;
        ApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? config["GEMINI_API_KEY"]
            ?? config["Gemini:ApiKey"];
        _logger = logger;
    }

    // A single generateContent call. Returns the text on 200, null on any failure.
    // temperature/maxTokens are per-call so the summarizer can run cooler than the brain.
    public async Task<string?> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        double temperature,
        int maxOutputTokens,
        CancellationToken ct)
    {
        if (!HasKey)
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
                SystemInstruction = string.IsNullOrEmpty(systemPrompt) ? null : new GeminiContent
                {
                    Parts = new[] { new GeminiPart { Text = systemPrompt } }
                },
                GenerationConfig = new GeminiGenerationConfig
                {
                    Temperature = temperature,
                    MaxOutputTokens = maxOutputTokens,
                    TopP = 0.95,
                    // 2.5-flash "thinks" by default and the thinking tokens count against
                    // maxOutputTokens - with small budgets it can burn them all and return
                    // no text at all (finishReason MAX_TOKENS, empty parts)
                    ThinkingConfig = new GeminiThinkingConfig { ThinkingBudget = 0 }
                }
            };

            var json = JsonSerializer.Serialize(body, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-goog-api-key", ApiKey);

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
        [JsonPropertyName("thinkingConfig")] public GeminiThinkingConfig? ThinkingConfig { get; set; }
    }

    private sealed class GeminiThinkingConfig
    {
        [JsonPropertyName("thinkingBudget")] public int ThinkingBudget { get; set; }
    }

    private sealed class GeminiResponse
    {
        [JsonPropertyName("candidates")] public GeminiCandidate[]? Candidates { get; set; }
    }

    private sealed class GeminiCandidate
    {
        [JsonPropertyName("content")] public GeminiContent? Content { get; set; }
    }
}
