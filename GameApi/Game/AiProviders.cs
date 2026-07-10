namespace GameApi.GameLoop;

// The narrow surface the game code calls: a system + user prompt in, text out (null on
// total failure). GeminiClient, OpenAiCompatClient, and the AiTextProvider failover
// orchestrator all speak this, so GeminiBrain / StyleSummarizer are agnostic about which
// provider actually answered. HasKey lets a caller skip work when nothing is configured.
public interface IAiTextProvider
{
    bool HasKey { get; }

    Task<string?> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        double temperature,
        int maxOutputTokens,
        CancellationToken ct);
}

// How a single provider's daily request quota rolls over. Gemini's free-tier RPD resets
// at midnight US Pacific; the OpenAI-compatible providers we approximate at plain UTC
// midnight. The counter is protective (keeps us off an already-spent free tier), not
// billing-exact, so an approximate boundary is fine.
public enum QuotaReset
{
    UtcMidnight,
    PacificMidnight
}

// Outcome of one provider call — richer than string? so the orchestrator can tell a
// rate-limit (spend the provider for the day) from a transient failure (trip the breaker
// after 3 in a row).
public enum AiCallOutcome
{
    Success,
    RateLimited,
    Failed
}

public readonly record struct AiCallResult(AiCallOutcome Outcome, string? Text, bool HasRetryInfo)
{
    public static AiCallResult Ok(string text) => new(AiCallOutcome.Success, text, false);
    public static AiCallResult Rate(bool hasRetryInfo) => new(AiCallOutcome.RateLimited, null, hasRetryInfo);
    public static readonly AiCallResult Fail = new(AiCallOutcome.Failed, null, false);
}

// One provider leg in the failover chain: identity + quota-reset semantics + a detailed
// call result on top of the plain IAiTextProvider surface.
public interface IAiProviderLeg : IAiTextProvider
{
    string ProviderName { get; }
    QuotaReset QuotaReset { get; }

    Task<AiCallResult> CallAsync(
        string systemPrompt,
        string userPrompt,
        double temperature,
        int maxOutputTokens,
        CancellationToken ct);
}
