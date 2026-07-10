using Microsoft.EntityFrameworkCore;
using GameApi.Data;

namespace GameApi.Retention;

// Daily cleanup job: hard-delete guest accounts that have been idle longer than
// Retention:GuestDays (default 30). Guests are throwaway identities — a name unused
// for a month gets swept, along with the light profile it accumulated.
//
// FK handling is done EXPLICITLY here rather than leaning on the database's cascade /
// set-null rules, so the exact same behavior holds under EF InMemory (the test DB,
// which enforces none of them) and Postgres:
//   * WritingSamples / StyleProfiles / PlayerStats  -> deleted with the guest.
//   * GameMessages.AuthorUserId                     -> set null (AuthorDisplayNameAtTime
//                                                      preserves who said it, so old
//                                                      transcripts stay intact).
//   * GamePlayers                                   -> deleted (UserId is non-nullable
//                                                      and the FK is Restrict, so the row
//                                                      must go; transcripts live in
//                                                      GameMessages and are untouched).
public class GuestRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<GuestRetentionService> _logger;

    public GuestRetentionService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<GuestRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    private int GuestDays => _config.GetValue<int?>("Retention:GuestDays") ?? 30;
    private double IntervalHours => _config.GetValue<double?>("Retention:IntervalHours") ?? 24;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(0.01, IntervalHours));
        while (!stoppingToken.IsCancellationRequested)
        {
            // Sleep first so a fresh boot (and every test run) doesn't purge on startup —
            // the job wakes on its cadence, and tests drive PurgeStaleGuestsAsync directly.
            try { await Task.Delay(interval, stoppingToken); }
            catch (TaskCanceledException) { break; }

            try
            {
                var removed = await PurgeStaleGuestsAsync(DateTime.UtcNow, stoppingToken);
                if (removed > 0)
                    _logger.LogInformation("Guest retention swept {Count} stale guest(s)", removed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Guest retention sweep failed");
            }
        }
    }

    // Hard-delete every guest idle since before (nowUtc - GuestDays). Returns how many
    // guest accounts were removed. Public + parameterized on nowUtc so tests can drive it
    // deterministically without waiting on the daily cadence.
    public async Task<int> PurgeStaleGuestsAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var cutoff = nowUtc - TimeSpan.FromDays(GuestDays);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();

        // Only sweep guests with a real, stale LastSeen. A null timestamp (rows predating
        // this feature) is treated as "unknown", never as "older than the cutoff".
        var staleIds = await db.Users
            .Where(u => u.IsGuest && u.LastSeenUtc != null && u.LastSeenUtc < cutoff)
            .Select(u => u.Id)
            .ToListAsync(ct);

        if (staleIds.Count == 0) return 0;

        foreach (var id in staleIds)
        {
            // Preserve transcripts: null the author link, keep the row + display name.
            var messages = await db.GameMessages
                .Where(m => m.AuthorUserId == id)
                .ToListAsync(ct);
            foreach (var m in messages)
                m.AuthorUserId = null;

            // GamePlayer rows can't be orphaned (non-nullable, Restrict) → delete them.
            var players = await db.GamePlayers.Where(p => p.UserId == id).ToListAsync(ct);
            db.GamePlayers.RemoveRange(players);

            // The guest's accumulated profile data goes with them.
            db.WritingSamples.RemoveRange(await db.WritingSamples.Where(s => s.UserId == id).ToListAsync(ct));
            db.StyleProfiles.RemoveRange(await db.StyleProfiles.Where(s => s.UserId == id).ToListAsync(ct));
            db.PlayerStats.RemoveRange(await db.PlayerStats.Where(s => s.UserId == id).ToListAsync(ct));
        }

        var users = await db.Users.Where(u => staleIds.Contains(u.Id)).ToListAsync(ct);
        db.Users.RemoveRange(users);

        await db.SaveChangesAsync(ct);
        return users.Count;
    }
}
