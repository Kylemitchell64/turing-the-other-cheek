using System.Collections.Concurrent;

namespace GameApi.Lobbies;

// Phase 33: per-user budget for the menu mascot's lobby chatter (crash-out flinches and
// pokes on someone else's character). SignalR traffic is exempt from the HTTP limiter
// (ADR 0008 — the live game can't run on 30/min), so these cosmetic broadcasts need their
// own cap or one client could flood a lobby's phones with "did you see that?". Keyed on
// the JWT user id, same reasoning as ADR 0008: a living room on one wifi IP still gets a
// bucket per player. Fixed windows per (user, kind); over budget is silently dropped by
// the caller — it's cosmetic, there's nothing to tell the player.
public class MascotRateLimiter
{
    public enum Kind { Crash, Poke }

    // A real crash-out takes 11 nudges plus a multi-second ending, so 3 a minute is
    // already generous; pokes are tap-speed, so they get a short, wider window.
    private static readonly Dictionary<Kind, (int PerWindow, TimeSpan Window)> Budgets = new()
    {
        [Kind.Crash] = (3, TimeSpan.FromMinutes(1)),
        [Kind.Poke] = (12, TimeSpan.FromSeconds(10)),
    };

    private readonly ConcurrentDictionary<(string UserId, Kind Kind), (DateTime WindowStart, int Count)> _hits = new();

    public bool TryTake(string userId, Kind kind) => TryTake(userId, kind, DateTime.UtcNow);

    // Clock-injectable for tests.
    public bool TryTake(string userId, Kind kind, DateTime now)
    {
        var (perWindow, window) = Budgets[kind];
        var updated = _hits.AddOrUpdate(
            (userId, kind),
            _ => (now, 1),
            (_, cur) => now - cur.WindowStart >= window ? (now, 1) : (cur.WindowStart, cur.Count + 1));
        return updated.Count <= perWindow;
    }
}
