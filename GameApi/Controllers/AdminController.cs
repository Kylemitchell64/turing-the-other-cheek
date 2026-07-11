using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GameApi.Admin;
using GameApi.Data;
using GameApi.GameLoop;
using GameApi.Lobbies;
using GameApi.Models;

namespace GameApi.Controllers;

// The phase-18 operator dashboard API. Every route is gated by the "AdminOnly" policy
// (Google account + allowlisted email, both baked into the JWT at login), so nothing here
// re-checks identity — the token already proved it. Read endpoints power the analytics
// tiles; the mutating ones (rewards, maintenance, restart, wipe) are the operator levers.
[Route("api/admin")]
[ApiController]
[Authorize(Policy = "AdminOnly")]
public class AdminController : ControllerBase
{
    private readonly GameContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly MaintenanceState _maintenance;
    private readonly LobbyStore _lobbies;
    private readonly AiProviderStats _aiStats;
    private readonly IConfiguration _config;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        GameContext db,
        UserManager<ApplicationUser> userManager,
        MaintenanceState maintenance,
        LobbyStore lobbies,
        AiProviderStats aiStats,
        IConfiguration config,
        IHostApplicationLifetime lifetime,
        ILogger<AdminController> logger)
    {
        _db = db;
        _userManager = userManager;
        _maintenance = maintenance;
        _lobbies = lobbies;
        _aiStats = aiStats;
        _config = config;
        _lifetime = lifetime;
        _logger = logger;
    }

    // The exact phrase the danger-zone wipe requires, typed literally by the operator.
    private const string WipeConfirmPhrase = "WIPE EVERYTHING";

    // The (lighter) typed confirmation guarding the bulk non-oauth purge.
    private const string PurgeGuestsPhrase = "DELETE GUESTS";

    // GET /api/admin/overview — headline stat tiles: account mix, game volume, live lobbies,
    // and one tile per AI provider (request count + circuit-breaker/quota state).
    [HttpGet("overview")]
    public async Task<IActionResult> Overview()
    {
        var now = DateTime.UtcNow;
        var todayStart = now.Date;
        var weekStart = now.AddDays(-7);

        var users = await _db.Users
            .Select(u => new { u.IsGuest, u.ExternalProvider })
            .ToListAsync();

        var guests = users.Count(u => u.IsGuest);
        var google = users.Count(u => u.ExternalProvider == "Google");
        var github = users.Count(u => u.ExternalProvider == "GitHub");
        // Registered = password accounts (not a guest, no external provider).
        var registered = users.Count(u => !u.IsGuest && string.IsNullOrEmpty(u.ExternalProvider));

        var startedAts = await _db.Games.Select(g => g.StartedAt).ToListAsync();

        var aiProviders = _aiStats.Snapshot(now).Select(p => new
        {
            provider = p.Provider,
            requestsToday = p.RequestsToday,
            successTotal = p.SuccessTotal,
            failureTotal = p.FailureTotal,
            rateLimitTotal = p.RateLimitTotal,
            failoverHops = p.FailoverHops,
            breakerOpen = p.BreakerOpen,
            exhaustedForDay = p.ExhaustedForDay
        }).ToList();

        return Ok(new
        {
            totalUsers = users.Count,
            guests,
            registered,
            google,
            github,
            oauth = google + github,
            gamesTotal = startedAts.Count,
            gamesToday = startedAts.Count(d => d >= todayStart),
            games7d = startedAts.Count(d => d >= weekStart),
            activeLobbies = _lobbies.All.Count(),
            aiProviders
        });
    }

    // GET /api/admin/timeline — games started per day for the last 30 calendar days (UTC),
    // zero-filled so the client can draw a continuous 30-bar SVG chart.
    [HttpGet("timeline")]
    public async Task<IActionResult> Timeline()
    {
        var today = DateTime.UtcNow.Date;
        var from = today.AddDays(-29);

        var startedAts = await _db.Games
            .Where(g => g.StartedAt >= from)
            .Select(g => g.StartedAt)
            .ToListAsync();

        var counts = startedAts
            .GroupBy(d => d.Date)
            .ToDictionary(g => g.Key, g => g.Count());

        var days = Enumerable.Range(0, 30)
            .Select(i => from.AddDays(i))
            .Select(d => new
            {
                date = d.ToString("yyyy-MM-dd"),
                count = counts.TryGetValue(d, out var c) ? c : 0
            })
            .ToList();

        return Ok(new { days });
    }

    // GET /api/admin/freetier — a rough "how close are the free tiers to their ceilings"
    // gauge across the AI providers, the Supabase DB, and the Render instance. Every number
    // is an estimate from what we can see locally (request counters, stored text bytes,
    // month-elapsed hours) — good enough to spot a tier filling up, never a billing source.
    [HttpGet("freetier")]
    public async Task<IActionResult> FreeTier()
    {
        var now = DateTime.UtcNow;
        var resources = new List<object>();
        var percents = new List<double>();

        void Add(string key, string label, double used, double limit, string unit)
        {
            var pct = limit <= 0 ? 0 : Math.Min(100, Math.Round(used / limit * 100, 1));
            percents.Add(pct);
            resources.Add(new { key, label, used = Math.Round(used, 1), limit, unit, percent = pct });
        }

        // AI providers: requests today vs a (config-overridable) free daily request cap.
        foreach (var p in _aiStats.Snapshot(now))
        {
            var cap = _config.GetValue<int?>($"FreeTier:Caps:{p.Provider}") ?? DefaultDailyCap(p.Provider);
            Add(p.Provider, $"{p.Provider} (req/day)", p.RequestsToday, cap, "req");
        }

        // Supabase free tier: 500 MB database. Estimate stored size from the big text
        // columns (writing samples + game messages) plus a flat per-user row overhead.
        var sampleBytes = await _db.WritingSamples.SumAsync(s => (long?)s.Text.Length) ?? 0;
        var messageBytes = await _db.GameMessages.SumAsync(m => (long?)m.Text.Length) ?? 0;
        var userCount = await _db.Users.CountAsync();
        var dbMb = (sampleBytes + messageBytes + userCount * 512L) / (1024.0 * 1024.0);
        var dbLimit = _config.GetValue<int?>("FreeTier:SupabaseMb") ?? 500;
        Add("supabase", "Supabase DB (MB)", dbMb, dbLimit, "MB");

        // Render free tier: 750 instance-hours / month. A continuously-running free service
        // accrues hours across the month, so month-elapsed hours approximates usage.
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var hoursUsed = (now - monthStart).TotalHours;
        var hoursLimit = _config.GetValue<int?>("FreeTier:RenderHours") ?? 750;
        Add("render", "Render (hrs/mo)", hoursUsed, hoursLimit, "hrs");

        var average = percents.Count == 0 ? 0 : Math.Round(percents.Average(), 1);
        return Ok(new { resources, average });
    }

    // A conservative default daily free-request cap per provider (overridable via config).
    private static int DefaultDailyCap(string provider) => provider.ToLowerInvariant() switch
    {
        "gemini" => 1500,
        "groq" => 14400,
        "cerebras" => 14400,
        _ => 1000
    };

    // GET /api/admin/users?search=&page=1&pageSize=20 — searchable, paged directory. Each
    // row carries the account tier, last-seen, games played, an estimated data-usage figure
    // (relative to the heaviest user, for a bar), and the rewards they currently hold.
    [HttpGet("users")]
    public async Task<IActionResult> Users(string? search, int page = 1, int pageSize = 20)
    {
        if (page < 1) page = 1;
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.Users.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u =>
                (u.DisplayName != null && u.DisplayName.ToLower().Contains(s)) ||
                (u.UserName != null && u.UserName.ToLower().Contains(s)));
        }

        var total = await query.CountAsync();

        var pageUsers = await query
            .OrderByDescending(u => u.LastSeenUtc ?? DateTime.MinValue)
            .ThenBy(u => u.UserName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                u.Id,
                u.DisplayName,
                u.UserName,
                u.Email,
                u.IsGuest,
                u.ExternalProvider,
                u.LastSeenUtc
            })
            .ToListAsync();

        var ids = pageUsers.Select(u => u.Id).ToList();

        // Data-usage estimate: total stored writing-sample bytes per user. The bar on the
        // client is relative to the global max, so heavy accounts stand out.
        var usageByUser = (await _db.WritingSamples
                .Where(s => ids.Contains(s.UserId))
                .GroupBy(s => s.UserId)
                .Select(g => new { UserId = g.Key, Bytes = g.Sum(s => s.Text.Length) })
                .ToListAsync())
            .ToDictionary(x => x.UserId, x => (long)x.Bytes);

        var maxDataUsage = await _db.WritingSamples
            .GroupBy(s => s.UserId)
            .Select(g => g.Sum(s => s.Text.Length))
            .OrderByDescending(x => x)
            .FirstOrDefaultAsync();

        var gamesByUser = (await _db.PlayerStats
                .Where(p => ids.Contains(p.UserId))
                .Select(p => new { p.UserId, p.GamesPlayed })
                .ToListAsync())
            .ToDictionary(x => x.UserId, x => x.GamesPlayed);

        var rewardsByUser = (await _db.UserRewards
                .Where(r => ids.Contains(r.UserId))
                .ToListAsync())
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = pageUsers.Select(u => new
        {
            id = u.Id,
            displayName = u.DisplayName ?? u.UserName,
            username = u.UserName,
            tier = TierOf(u.IsGuest, u.ExternalProvider, u.Email),
            lastSeen = u.LastSeenUtc,
            gamesPlayed = gamesByUser.TryGetValue(u.Id, out var g) ? g : 0,
            dataUsage = usageByUser.TryGetValue(u.Id, out var b) ? b : 0L,
            rewards = SummarizeRewards(rewardsByUser.TryGetValue(u.Id, out var rl) ? rl : new List<UserReward>())
        });

        return Ok(new { page, pageSize, total, maxDataUsage = (long)maxDataUsage, users = rows });
    }

    // GET /api/admin/users/{id} — the per-user synopsis behind a clicked row. Everything the
    // operator needs to understand an account at a glance; deliberately NOT the raw sample
    // TEXT (that's private) — just counts + total characters so the row can say "12 samples,
    // 3.4k chars" without exposing what someone wrote.
    [HttpGet("users/{id}")]
    public async Task<IActionResult> UserProfile(string id)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound(new { error = "no such user" });

        var stats = await _db.PlayerStats.FirstOrDefaultAsync(p => p.UserId == id);
        var sampleCount = await _db.WritingSamples.CountAsync(s => s.UserId == id);
        var sampleChars = await _db.WritingSamples.Where(s => s.UserId == id).SumAsync(s => (long?)s.Text.Length) ?? 0;
        var rewards = await _db.UserRewards.Where(r => r.UserId == id).ToListAsync();

        var crews = await _db.CrewMembers
            .Where(m => m.UserId == id)
            .Select(m => new
            {
                name = m.Crew!.Name,
                joinCode = m.Crew.JoinCode,
                isOwner = m.Crew.OwnerUserId == id,
                joinedAt = m.JoinedAt
            })
            .ToListAsync();

        return Ok(new
        {
            id = user.Id,
            displayName = user.DisplayName ?? user.UserName,
            username = user.UserName,
            email = user.Email,
            tier = TierOf(user.IsGuest, user.ExternalProvider, user.Email),
            provider = user.IsGuest ? "guest" : (string.IsNullOrEmpty(user.ExternalProvider) ? "password" : user.ExternalProvider),
            isGuest = user.IsGuest,
            isAdmin = AdminEmails.IsAdmin(_config, user.Email, user.ExternalProvider),
            lastSeen = user.LastSeenUtc,
            gamesPlayed = stats?.GamesPlayed ?? 0,
            detectorWins = stats?.DetectorWins ?? 0,
            timesFooled = stats?.TimesFooled ?? 0,
            timesReadByAi = stats?.TimesReadByAi ?? 0,
            aiSurvivalGamesWitnessed = stats?.AiSurvivalGamesWitnessed ?? 0,
            sampleCount,
            sampleChars,
            hasCharacter = !string.IsNullOrEmpty(user.CharacterJson),
            crews,
            rewards = SummarizeRewards(rewards)
        });
    }

    // POST /api/admin/users/{id}/rewards  { kind } — grant a cosmetic unlock or cheat card.
    // Cosmetic grants are idempotent (a second grant of the same unlock is a no-op); cheat
    // cards stack (each is one consumable +1-token bonus).
    [HttpPost("users/{id}/rewards")]
    public async Task<IActionResult> GrantReward(string id, [FromBody] GrantRewardRequest req)
    {
        var kind = req?.Kind?.Trim() ?? "";
        if (!RewardKinds.IsGrantable(kind))
            return BadRequest(new { error = "not a grantable reward" });

        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound(new { error = "no such user" });

        // Cosmetics are permanent one-per-kind unlocks — skip a duplicate grant.
        if (kind != RewardKinds.CheatCard)
        {
            var already = await _db.UserRewards.AnyAsync(r => r.UserId == id && r.Kind == kind);
            if (!already)
                _db.UserRewards.Add(new UserReward { UserId = id, Kind = kind, GrantedAt = DateTime.UtcNow });
        }
        else
        {
            _db.UserRewards.Add(new UserReward { UserId = id, Kind = kind, GrantedAt = DateTime.UtcNow });
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Admin granted {Kind} to {User}", kind, id);

        var rewards = await _db.UserRewards.Where(r => r.UserId == id).ToListAsync();
        return Ok(SummarizeRewards(rewards));
    }

    // DELETE /api/admin/users/{id}/rewards?kind=... — revoke. For a cosmetic unlock, drops
    // the matching grant; for a cheat card, spends one unconsumed card. A saved premium look
    // that loses its unlock still displays fine — the next save just revalidates against it.
    [HttpDelete("users/{id}/rewards")]
    public async Task<IActionResult> RevokeReward(string id, string? kind)
    {
        kind = kind?.Trim() ?? "";
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound(new { error = "no such user" });

        if (kind == RewardKinds.CheatCard)
        {
            var card = await _db.UserRewards
                .Where(r => r.UserId == id && r.Kind == RewardKinds.CheatCard && r.ConsumedAt == null)
                .OrderByDescending(r => r.GrantedAt)
                .FirstOrDefaultAsync();
            if (card != null) _db.UserRewards.Remove(card);
        }
        else
        {
            var matches = await _db.UserRewards
                .Where(r => r.UserId == id && r.Kind == kind)
                .ToListAsync();
            _db.UserRewards.RemoveRange(matches);
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Admin revoked {Kind} from {User}", kind, id);

        var rewards = await _db.UserRewards.Where(r => r.UserId == id).ToListAsync();
        return Ok(SummarizeRewards(rewards));
    }

    // POST /api/admin/maintenance  { on, message } — flip the process-wide pause. When on,
    // the hub refuses new lobbies/joins/starts and /api/status banners the message.
    [HttpPost("maintenance")]
    public IActionResult SetMaintenance([FromBody] MaintenanceRequest req)
    {
        _maintenance.Set(req?.On ?? false, req?.Message);
        var (on, message) = _maintenance.Snapshot();
        _logger.LogInformation("Admin set maintenance={On}", on);
        return Ok(new { maintenance = on, message });
    }

    // POST /api/admin/restart — self-restart. Returns immediately, then exits the process a
    // beat later so Render's supervisor relaunches a fresh container. In-memory state
    // (lobbies, maintenance flag) resets, which is the intended "clean slate" behavior.
    [HttpPost("restart")]
    public IActionResult Restart()
    {
        _logger.LogWarning("Admin requested a self-restart");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            _lifetime.StopApplication();
            await Task.Delay(TimeSpan.FromSeconds(2));
            Environment.Exit(0);
        });
        return Accepted(new { restarting = true });
    }

    // DELETE /api/admin/users/{id} — remove one account and everything hanging off it. Admin
    // accounts (allowlisted email) are never deletable. Cascades cleanly via PurgeAccountsAsync.
    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(string id)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound(new { error = "no such user" });
        if (AdminEmails.IsAdmin(_config, user.Email, user.ExternalProvider))
            return BadRequest(new { error = "admin accounts can't be deleted" });

        var name = user.DisplayName ?? user.UserName;
        await PurgeAccountsAsync(new List<ApplicationUser> { user });
        _logger.LogWarning("Admin deleted account {User}", id);
        return Ok(new { deleted = true, displayName = name });
    }

    // POST /api/admin/users/purge-nonoauth  { confirm } — bulk-delete every account with no
    // external provider (guests + legacy password logins). Requires the exact confirm phrase.
    // Admins are OAuth by definition so they're spared; we guard on the allowlist anyway.
    [HttpPost("users/purge-nonoauth")]
    public async Task<IActionResult> PurgeNonOauth([FromBody] WipeRequest req)
    {
        if (req?.Confirm != PurgeGuestsPhrase)
            return BadRequest(new { error = $"confirmation must be exactly \"{PurgeGuestsPhrase}\"" });

        var all = await _db.Users.ToListAsync();
        var doomed = all
            .Where(u => string.IsNullOrEmpty(u.ExternalProvider))
            .Where(u => !AdminEmails.IsAdmin(_config, u.Email, u.ExternalProvider))
            .ToList();

        await PurgeAccountsAsync(doomed);
        _logger.LogWarning("Admin purged {Count} non-oauth accounts", doomed.Count);
        return Ok(new { deleted = doomed.Count });
    }

    // POST /api/admin/wipe  { confirm } — the danger-zone reset. Requires the exact confirm
    // phrase. Deletes all game history and every non-admin account (+ their data); admin
    // accounts are spared so the operator can still sign in afterward.
    [HttpPost("wipe")]
    public async Task<IActionResult> Wipe([FromBody] WipeRequest req)
    {
        if (req?.Confirm != WipeConfirmPhrase)
            return BadRequest(new { error = $"confirmation must be exactly \"{WipeConfirmPhrase}\"" });

        // Games first so their child rows (players/messages/prompts) go with them and no
        // GamePlayer.UserId restrict-FK blocks a user delete below.
        _db.GameMessages.RemoveRange(await _db.GameMessages.ToListAsync());
        _db.GameRoundPrompts.RemoveRange(await _db.GameRoundPrompts.ToListAsync());
        _db.GamePlayers.RemoveRange(await _db.GamePlayers.ToListAsync());
        _db.Games.RemoveRange(await _db.Games.ToListAsync());
        _db.WritingSamples.RemoveRange(await _db.WritingSamples.ToListAsync());
        _db.StyleProfiles.RemoveRange(await _db.StyleProfiles.ToListAsync());
        _db.PlayerStats.RemoveRange(await _db.PlayerStats.ToListAsync());
        _db.UserRewards.RemoveRange(await _db.UserRewards.ToListAsync());
        await _db.SaveChangesAsync();

        var allUsers = await _db.Users.ToListAsync();
        var doomed = allUsers
            .Where(u => !AdminEmails.IsAdmin(_config, u.Email, u.ExternalProvider))
            .ToList();
        _db.Users.RemoveRange(doomed);
        await _db.SaveChangesAsync();

        _logger.LogWarning("Admin wiped the database: {Count} accounts removed", doomed.Count);
        return Ok(new { wiped = true, accountsRemoved = doomed.Count, adminsKept = allUsers.Count - doomed.Count });
    }

    // --- helpers ---

    // Cleanly remove a set of accounts and everything hanging off them, working around the
    // two RESTRICT foreign keys (GamePlayer.UserId and Crew.OwnerUserId) that would otherwise
    // block the delete. Cascade FKs (samples, style, stats, rewards, crew memberships) would
    // go on their own, but we clear them explicitly too so behavior is identical on the
    // in-memory provider the tests use. Assumes the caller already spared admins.
    private async Task PurgeAccountsAsync(List<ApplicationUser> users)
    {
        if (users.Count == 0) return;
        var ids = users.Select(u => u.Id).ToHashSet();

        // RESTRICT #1: participation rows. Drop the user's seats; the games themselves stay.
        var playerRows = await _db.GamePlayers.Where(p => ids.Contains(p.UserId)).ToListAsync();
        _db.GamePlayers.RemoveRange(playerRows);

        // Authored messages are SetNull-on-delete — do it explicitly. The message stays as
        // history, just no longer attributed to a now-deleted account.
        var authored = await _db.GameMessages
            .Where(m => m.AuthorUserId != null && ids.Contains(m.AuthorUserId!))
            .ToListAsync();
        foreach (var m in authored) m.AuthorUserId = null;

        // RESTRICT #2: owned crews. Hand ownership to the oldest OTHER member; disband if none.
        var ownedCrews = await _db.Crews.Include(c => c.Members)
            .Where(c => ids.Contains(c.OwnerUserId))
            .ToListAsync();
        foreach (var crew in ownedCrews)
        {
            var heir = crew.Members
                .Where(m => !ids.Contains(m.UserId))
                .OrderBy(m => m.JoinedAt)
                .FirstOrDefault();
            if (heir != null) crew.OwnerUserId = heir.UserId;
            else _db.Crews.Remove(crew); // cascades its CrewMembers
        }

        // Cascade children, cleared explicitly for provider-agnostic behavior.
        _db.CrewMembers.RemoveRange(await _db.CrewMembers.Where(m => ids.Contains(m.UserId)).ToListAsync());
        _db.WritingSamples.RemoveRange(await _db.WritingSamples.Where(s => ids.Contains(s.UserId)).ToListAsync());
        _db.StyleProfiles.RemoveRange(await _db.StyleProfiles.Where(s => ids.Contains(s.UserId)).ToListAsync());
        _db.PlayerStats.RemoveRange(await _db.PlayerStats.Where(s => ids.Contains(s.UserId)).ToListAsync());
        _db.UserRewards.RemoveRange(await _db.UserRewards.Where(r => ids.Contains(r.UserId)).ToListAsync());
        await _db.SaveChangesAsync();

        _db.Users.RemoveRange(users);
        await _db.SaveChangesAsync();
    }

    private string TierOf(bool isGuest, string? provider, string? email)
    {
        if (AdminEmails.IsAdmin(_config, email, provider)) return "admin";
        if (isGuest) return "guest";
        if (string.Equals(provider, "Google", StringComparison.OrdinalIgnoreCase)) return "google";
        if (string.Equals(provider, "GitHub", StringComparison.OrdinalIgnoreCase)) return "github";
        return "registered";
    }

    private static object SummarizeRewards(List<UserReward> rewards)
    {
        var outfits = new SortedSet<int>();
        var accessories = new SortedSet<int>();
        var cheatCards = 0;
        foreach (var r in rewards)
        {
            if (r.Kind == RewardKinds.CheatCard)
            {
                if (r.ConsumedAt == null) cheatCards++;
            }
            else if (RewardKinds.TryOutfit(r.Kind, out var o)) outfits.Add(o);
            else if (RewardKinds.TryAccessory(r.Kind, out var a)) accessories.Add(a);
        }
        return new
        {
            outfits = outfits.ToArray(),
            accessories = accessories.ToArray(),
            cheatCards
        };
    }

    public record GrantRewardRequest(string? Kind);
    public record MaintenanceRequest(bool On, string? Message);
    public record WipeRequest(string? Confirm);
}
