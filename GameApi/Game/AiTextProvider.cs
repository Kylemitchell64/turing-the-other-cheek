namespace GameApi.GameLoop;

// The failover orchestrator. Presents the same GenerateAsync surface the game code has
// always called, but fans the request across an ordered chain of provider legs
// (Gemini → Groq → Cerebras). It skips legs with no key, skips legs the circuit breaker
// or daily-quota tracker has marked unavailable, and hops to the next leg on any failure,
// logging every hop. Returns the first success, or null when the whole chain is spent —
// at which point GeminiBrain's canned-fallback path takes over as the last resort.
public class AiTextProvider : IAiTextProvider
{
    private readonly IReadOnlyList<IAiProviderLeg> _legs;
    private readonly AiProviderStats _stats;
    private readonly ILogger<AiTextProvider> _logger;
    private readonly Func<DateTime> _clock;

    public AiTextProvider(
        IEnumerable<IAiProviderLeg> legs,
        AiProviderStats stats,
        ILogger<AiTextProvider> logger,
        Func<DateTime>? clock = null)
    {
        // Only legs with a configured key participate; order is preserved.
        _legs = legs.Where(l => l.HasKey).ToList();
        _stats = stats;
        _logger = logger;
        _clock = clock ?? (() => DateTime.UtcNow);

        foreach (var leg in _legs)
            _stats.Register(leg.ProviderName, leg.QuotaReset);
    }

    public bool HasKey => _legs.Count > 0;

    public IReadOnlyList<string> ActiveProviders => _legs.Select(l => l.ProviderName).ToList();

    // The readable snapshot for the Phase 18 admin dashboard.
    public IReadOnlyList<ProviderStatsSnapshot> StatsSnapshot() => _stats.Snapshot(_clock());

    public async Task<string?> GenerateAsync(
        string systemPrompt, string userPrompt, double temperature, int maxOutputTokens, CancellationToken ct)
    {
        if (_legs.Count == 0)
            return null;

        for (var i = 0; i < _legs.Count; i++)
        {
            var leg = _legs[i];
            var now = _clock();

            if (!_stats.IsAvailable(leg.ProviderName, now))
            {
                _logger.LogInformation(
                    "AI failover: skipping {Provider} (unavailable: breaker open or quota spent) → {Next}",
                    leg.ProviderName, NextName(i));
                _stats.RecordFailover(leg.ProviderName);
                continue;
            }

            _stats.RecordAttempt(leg.ProviderName, now);

            AiCallResult result;
            try
            {
                result = await leg.CallAsync(systemPrompt, userPrompt, temperature, maxOutputTokens, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // the round ended — let the caller unwind, don't burn the chain
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI provider {Provider} threw", leg.ProviderName);
                result = AiCallResult.Fail;
            }

            var after = _clock();
            switch (result.Outcome)
            {
                case AiCallOutcome.Success when !string.IsNullOrWhiteSpace(result.Text):
                    _stats.RecordSuccess(leg.ProviderName);
                    return result.Text;

                case AiCallOutcome.RateLimited:
                    _stats.RecordRateLimited(leg.ProviderName, after, tripBreaker: result.HasRetryInfo);
                    _logger.LogWarning(
                        "AI failover: {Provider} rate-limited (exhausted for the day) → {Next}",
                        leg.ProviderName, NextName(i));
                    _stats.RecordFailover(leg.ProviderName);
                    break;

                default:
                    _stats.RecordFailure(leg.ProviderName, after, "call failed / empty");
                    _logger.LogWarning(
                        "AI failover: {Provider} failed → {Next}", leg.ProviderName, NextName(i));
                    _stats.RecordFailover(leg.ProviderName);
                    break;
            }
        }

        _logger.LogWarning("AI failover: all {Count} providers exhausted; caller falls back", _legs.Count);
        return null;
    }

    private string NextName(int index) =>
        index + 1 < _legs.Count ? _legs[index + 1].ProviderName : "(canned fallback)";
}
