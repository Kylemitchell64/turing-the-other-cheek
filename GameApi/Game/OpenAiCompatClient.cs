using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameApi.GameLoop;

// A generic OpenAI-compatible chat client. Groq and Cerebras both speak the same wire
// protocol: POST {baseUrl}/chat/completions with an "Authorization: Bearer {key}" header,
// a {model, messages[], temperature, max_tokens} body, and choices[0].message.content in
// the response. One class covers both, parameterised by (name, baseUrl, model, key name).
//
// Same shape as GeminiClient so it slots into the failover chain as a leg: returns the
// text on 200, a RateLimited result on 429/quota, Failed on anything else.
public class OpenAiCompatClient : IAiProviderLeg
{
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OpenAiCompatClient> _logger;

    public string ProviderName { get; }
    public QuotaReset QuotaReset { get; }
    public string? ApiKey { get; }
    public bool HasKey => !string.IsNullOrEmpty(ApiKey);

    public OpenAiCompatClient(
        string providerName,
        string baseUrl,
        string model,
        string apiKeyName,
        QuotaReset quotaReset,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<OpenAiCompatClient> logger)
    {
        ProviderName = providerName;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        QuotaReset = quotaReset;
        _httpFactory = httpFactory;
        _logger = logger;
        ApiKey = Environment.GetEnvironmentVariable(apiKeyName) ?? config[apiKeyName];
    }

    public async Task<string?> GenerateAsync(
        string systemPrompt, string userPrompt, double temperature, int maxOutputTokens, CancellationToken ct) =>
        (await CallAsync(systemPrompt, userPrompt, temperature, maxOutputTokens, ct)).Text;

    public async Task<AiCallResult> CallAsync(
        string systemPrompt, string userPrompt, double temperature, int maxOutputTokens, CancellationToken ct)
    {
        if (!HasKey)
        {
            _logger.LogWarning("{Provider}: no API key configured", ProviderName);
            return AiCallResult.Fail;
        }

        try
        {
            var messages = new List<ChatMessage>();
            if (!string.IsNullOrEmpty(systemPrompt))
                messages.Add(new ChatMessage { Role = "system", Content = systemPrompt });
            messages.Add(new ChatMessage { Role = "user", Content = userPrompt });

            var body = new ChatRequest
            {
                Model = _model,
                Messages = messages,
                Temperature = temperature,
                MaxTokens = maxOutputTokens
            };

            var json = JsonSerializer.Serialize(body, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

            var client = _httpFactory.CreateClient(ProviderName);
            using var resp = await client.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
            {
                // 429 (or a 4xx quota response) means we've spent this provider's free tier
                // for the day — surface it distinctly so the chain marks it exhausted.
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var hasRetryInfo = resp.Headers.RetryAfter != null;
                    _logger.LogWarning("{Provider}: 429 rate-limited (retryInfo={RetryInfo})", ProviderName, hasRetryInfo);
                    return AiCallResult.Rate(hasRetryInfo);
                }
                _logger.LogWarning("{Provider}: HTTP {Status}", ProviderName, (int)resp.StatusCode);
                return AiCallResult.Fail;
            }

            var respJson = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<ChatResponse>(respJson, JsonOpts);
            var textOut = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
            return string.IsNullOrWhiteSpace(textOut) ? AiCallResult.Fail : AiCallResult.Ok(textOut);
        }
        catch (OperationCanceledException)
        {
            return AiCallResult.Fail;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Provider}: request threw", ProviderName);
            return AiCallResult.Fail;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    // ---- request/response DTOs (OpenAI chat/completions) ----

    private sealed class ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = default!;
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = default!;
        [JsonPropertyName("temperature")] public double Temperature { get; set; }
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = default!;
        [JsonPropertyName("content")] public string Content { get; set; } = default!;
    }

    private sealed class ChatResponse
    {
        [JsonPropertyName("choices")] public ChatChoice[]? Choices { get; set; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
    }
}
