using Microsoft.EntityFrameworkCore;

namespace GameApi.Data;

// Per-tier rolling cap on how many WritingSamples an account keeps. Guests keep a light
// profile (10 samples); OAuth/password accounts keep a deep one (200). Enforced on every
// insert path — manual paste AND post-game harvesting — by dropping the oldest samples
// beyond the cap. The condensed StyleProfiles summary is kept for both tiers regardless;
// it IS the distilled data, so trimming raw samples never erases what the AI learned.
public static class SampleCaps
{
    public const int GuestCap = 10;
    public const int UserCap = 200;

    public static int CapFor(bool isGuest) => isGuest ? GuestCap : UserCap;

    // Trim userId's samples to their tier cap, deleting the oldest first. Assumes any new
    // sample has already been added + saved. No-op when under cap. Saves its own changes.
    public static async Task EnforceAsync(GameContext db, string userId, bool isGuest, CancellationToken ct = default)
    {
        var cap = CapFor(isGuest);
        var count = await db.WritingSamples.CountAsync(s => s.UserId == userId, ct);
        if (count <= cap) return;

        var overflow = await db.WritingSamples
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .Take(count - cap)
            .ToListAsync(ct);

        db.WritingSamples.RemoveRange(overflow);
        await db.SaveChangesAsync(ct);
    }

    // Same, but resolves the tier from the account itself (an unknown user defaults to
    // the non-guest cap, which is the safe/larger bound).
    public static async Task EnforceAsync(GameContext db, string userId, CancellationToken ct = default)
    {
        var isGuest = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.IsGuest)
            .FirstOrDefaultAsync(ct);
        await EnforceAsync(db, userId, isGuest, ct);
    }
}
