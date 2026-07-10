namespace GameApi.GameLoop;

// Per-provider health + usage state for the failover chain, and the single readable
// snapshot the Phase 18 admin dashboard will consume. Injectable singleton; every method
// is thread-safe (one lock) and takes an explicit clock so tests drive the breaker /
// daily-reset boundaries deterministically.
//
// Tracks, per provider: a consecutive-failure circuit breaker (3 fails opens it for
// 5 min), a daily request counter that resets at the provider's quota boundary, and an
// exhausted-for-the-day flag flipped by a 429/quota response.
public class AiProviderStats
{
    private const int BreakerThreshold = 3;
    private static readonly TimeSpan BreakerCooldown = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    private sealed class State
    {
        public string Name = "";
        public QuotaReset Reset;
        public int ConsecutiveFailures;
        public DateTime? BreakerOpenUntil;
        public DateOnly QuotaDay;
        public bool ExhaustedForDay;
        public long RequestsToday;
        public long SuccessTotal;
        public long FailureTotal;
        public long RateLimitTotal;
        public long FailoverHops;
        public DateTime? LastFailureUtc;
        public string? LastFailureReason;
    }

    // Called once per leg at orchestrator construction so the snapshot lists providers in
    // chain order even before any traffic. Idempotent.
    public void Register(string name, QuotaReset reset)
    {
        lock (_lock)
        {
            if (_states.ContainsKey(name)) return;
            _states[name] = new State
            {
                Name = name,
                Reset = reset,
                QuotaDay = QuotaDayFor(reset, DateTime.UtcNow)
            };
            _order.Add(name);
        }
    }

    // Is this provider usable right now? (Key-present is the caller's check.) Rolls the
    // daily counter over and lazily closes an expired breaker as a side effect.
    public bool IsAvailable(string name, DateTime now)
    {
        lock (_lock)
        {
            var s = Get(name);
            Rollover(s, now);
            if (s.ExhaustedForDay) return false;
            if (s.BreakerOpenUntil is { } until)
            {
                if (now < until) return false;
                s.BreakerOpenUntil = null;
                s.ConsecutiveFailures = 0;
            }
            return true;
        }
    }

    public void RecordAttempt(string name, DateTime now)
    {
        lock (_lock)
        {
            var s = Get(name);
            Rollover(s, now);
            s.RequestsToday++;
        }
    }

    public void RecordSuccess(string name)
    {
        lock (_lock)
        {
            var s = Get(name);
            s.ConsecutiveFailures = 0;
            s.BreakerOpenUntil = null;
            s.SuccessTotal++;
        }
    }

    public void RecordFailure(string name, DateTime now, string reason)
    {
        lock (_lock)
        {
            var s = Get(name);
            s.FailureTotal++;
            s.LastFailureUtc = now;
            s.LastFailureReason = reason;
            s.ConsecutiveFailures++;
            if (s.ConsecutiveFailures >= BreakerThreshold)
                s.BreakerOpenUntil = now.Add(BreakerCooldown);
        }
    }

    // A 429/quota response spends the provider for the rest of its quota day. When the
    // 429 carried retry info it also counts toward the breaker (repeated rate-limits with
    // backoff hints mean the provider is genuinely unhealthy, not just capped).
    public void RecordRateLimited(string name, DateTime now, bool tripBreaker)
    {
        lock (_lock)
        {
            var s = Get(name);
            Rollover(s, now);
            s.RateLimitTotal++;
            s.ExhaustedForDay = true;
            s.LastFailureUtc = now;
            s.LastFailureReason = "429 / quota";
            if (tripBreaker)
            {
                s.ConsecutiveFailures++;
                if (s.ConsecutiveFailures >= BreakerThreshold)
                    s.BreakerOpenUntil = now.Add(BreakerCooldown);
            }
        }
    }

    public void RecordFailover(string name)
    {
        lock (_lock) { Get(name).FailoverHops++; }
    }

    // A pure read: computes display values against `now` (rolled-over counts, live breaker
    // state) without mutating stored state.
    public IReadOnlyList<ProviderStatsSnapshot> Snapshot(DateTime now)
    {
        lock (_lock)
        {
            var list = new List<ProviderStatsSnapshot>(_order.Count);
            foreach (var name in _order)
            {
                var s = _states[name];
                var sameDay = QuotaDayFor(s.Reset, now) == s.QuotaDay;
                var breakerOpen = s.BreakerOpenUntil is { } u && now < u;
                list.Add(new ProviderStatsSnapshot(
                    Provider: s.Name,
                    QuotaReset: s.Reset,
                    RequestsToday: sameDay ? s.RequestsToday : 0,
                    SuccessTotal: s.SuccessTotal,
                    FailureTotal: s.FailureTotal,
                    RateLimitTotal: s.RateLimitTotal,
                    FailoverHops: s.FailoverHops,
                    ConsecutiveFailures: s.ConsecutiveFailures,
                    BreakerOpen: breakerOpen,
                    BreakerOpenUntil: breakerOpen ? s.BreakerOpenUntil : null,
                    ExhaustedForDay: sameDay && s.ExhaustedForDay));
            }
            return list;
        }
    }

    private State Get(string name) =>
        _states.TryGetValue(name, out var s)
            ? s
            : throw new InvalidOperationException($"Unknown AI provider '{name}' (register it first).");

    private void Rollover(State s, DateTime now)
    {
        var day = QuotaDayFor(s.Reset, now);
        if (day == s.QuotaDay) return;
        s.QuotaDay = day;
        s.RequestsToday = 0;
        s.ExhaustedForDay = false;
    }

    // Pacific is approximated as a fixed UTC-8 offset — a day-boundary this coarse is fine
    // for a protective counter (we never bill on it).
    private static DateOnly QuotaDayFor(QuotaReset reset, DateTime utcNow) =>
        DateOnly.FromDateTime(reset == QuotaReset.PacificMidnight ? utcNow.AddHours(-8) : utcNow);
}

// One provider's line in the readable stats snapshot.
public record ProviderStatsSnapshot(
    string Provider,
    QuotaReset QuotaReset,
    long RequestsToday,
    long SuccessTotal,
    long FailureTotal,
    long RateLimitTotal,
    long FailoverHops,
    int ConsecutiveFailures,
    bool BreakerOpen,
    DateTime? BreakerOpenUntil,
    bool ExhaustedForDay);
