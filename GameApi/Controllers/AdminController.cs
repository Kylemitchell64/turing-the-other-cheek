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
    private readonly SelfCheckService _selfCheck;
    private readonly AdminCheatState _cheats;

    public AdminController(
        GameContext db,
        UserManager<ApplicationUser> userManager,
        MaintenanceState maintenance,
        LobbyStore lobbies,
        AiProviderStats aiStats,
        IConfiguration config,
        IHostApplicationLifetime lifetime,
        SelfCheckService selfCheck,
        AdminCheatState cheats,
        ILogger<AdminController> logger)
    {
        _selfCheck = selfCheck;
        _cheats = cheats;
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

    // GET /api/admin/users?search=&filter=&sort=&page=1&pageSize=20 — searchable, filterable,
    // sortable, paged directory (phase 30). Each row carries the account tier, last-seen,
    // games played, the account's stored bytes (samples + messages + profile + character,
    // plus a flat row overhead) and the rewards held.
    //   filter: all | inactive (no sign-in for 30+ days) | guests | oauth |
    //           safe-delete (guest, never played, no samples, not seen in 24h — probes and
    //           abandoned quick-plays; deleting them loses nothing)
    //   sort:   lastSeen (default) | storage | games | name | created
    [HttpGet("users")]
    public async Task<IActionResult> Users(string? search, string? filter, string? sort, int page = 1, int pageSize = 20)
    {
        if (page < 1) page = 1;
        pageSize = Math.Clamp(pageSize, 1, 100);
        var now = DateTime.UtcNow;

        var query = _db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u =>
                (u.DisplayName != null && u.DisplayName.ToLower().Contains(s)) ||
                (u.UserName != null && u.UserName.ToLower().Contains(s)));
        }

        // Small table (free tier): pull the candidate rows, then join the per-user tallies
        // in memory. Every tally is one grouped query, not one per user.
        var all = await query
            .Select(u => new { u.Id, u.DisplayName, u.UserName, u.Email, u.IsGuest, u.ExternalProvider, u.LastSeenUtc,
                CharBytes = u.CharacterJson == null ? 0 : u.CharacterJson.Length })
            .ToListAsync();

        var tallies = await UserTalliesAsync(all.Select(u => u.Id).ToList());

        var rows = all.Select(u =>
        {
            var t = tallies.TryGetValue(u.Id, out var tt) ? tt : new UserTally();
            var storage = t.SampleBytes + t.MessageBytes + t.ProfileBytes + u.CharBytes + 512L;
            var safeDelete = u.IsGuest && t.Games == 0 && t.Samples == 0
                && (u.LastSeenUtc == null || u.LastSeenUtc < now.AddHours(-24));
            return new UserRow(
                u.Id, u.DisplayName ?? u.UserName ?? "?", u.UserName ?? "", u.IsGuest, u.ExternalProvider,
                TierOf(u.IsGuest, u.ExternalProvider, u.Email), u.LastSeenUtc, t.Games, storage, t.Samples,
                t.MessageBytes, safeDelete);
        });

        rows = (filter ?? "all").ToLowerInvariant() switch
        {
            "inactive" => rows.Where(r => r.LastSeen == null || r.LastSeen < now.AddDays(-30)),
            "guests" => rows.Where(r => r.IsGuest),
            "oauth" => rows.Where(r => !string.IsNullOrEmpty(r.ExternalProvider)),
            "safe-delete" => rows.Where(r => r.SafeDelete),
            _ => rows,
        };

        rows = (sort ?? "lastseen").ToLowerInvariant() switch
        {
            "storage" => rows.OrderByDescending(r => r.StorageBytes).ThenBy(r => r.Username),
            "games" => rows.OrderByDescending(r => r.GamesPlayed).ThenBy(r => r.Username),
            "name" => rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderByDescending(r => r.LastSeen ?? DateTime.MinValue).ThenBy(r => r.Username),
        };

        var list = rows.ToList();
        var total = list.Count;
        var pageRows = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var ids = pageRows.Select(r => r.Id).ToList();
        var maxStorage = list.Count == 0 ? 0 : list.Max(r => r.StorageBytes);

        var rewardsByUser = (await _db.UserRewards
                .AsNoTracking()
                .Where(r => ids.Contains(r.UserId))
                .ToListAsync())
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var users = pageRows.Select(r => new
        {
            id = r.Id,
            displayName = r.DisplayName,
            username = r.Username,
            tier = r.Tier,
            lastSeen = r.LastSeen,
            gamesPlayed = r.GamesPlayed,
            dataUsage = r.StorageBytes,
            samples = r.Samples,
            safeDelete = r.SafeDelete,
            rewards = SummarizeRewards(rewardsByUser.TryGetValue(r.Id, out var rl) ? rl : new List<UserReward>())
        });

        return Ok(new { page, pageSize, total, maxDataUsage = maxStorage, users });
    }

    private sealed record UserRow(
        string Id, string DisplayName, string Username, bool IsGuest, string? ExternalProvider, string Tier,
        DateTime? LastSeen, int GamesPlayed, long StorageBytes, int Samples, long MessageBytes, bool SafeDelete);

    private sealed class UserTally
    {
        public int Games; public int Samples; public long SampleBytes; public long MessageBytes; public long ProfileBytes;
    }

    // Per-user counts + bytes in four grouped queries (samples, messages, profiles, stats).
    private async Task<Dictionary<string, UserTally>> UserTalliesAsync(List<string> ids)
    {
        var t = new Dictionary<string, UserTally>(StringComparer.Ordinal);
        UserTally For(string id) { if (!t.TryGetValue(id, out var x)) { x = new UserTally(); t[id] = x; } return x; }

        foreach (var g in await _db.WritingSamples.AsNoTracking().Where(s => ids.Contains(s.UserId))
                     .GroupBy(s => s.UserId).Select(g => new { g.Key, N = g.Count(), B = g.Sum(s => s.Text.Length) }).ToListAsync())
        { var x = For(g.Key); x.Samples = g.N; x.SampleBytes = g.B; }

        foreach (var g in await _db.GameMessages.AsNoTracking().Where(m => m.AuthorUserId != null && ids.Contains(m.AuthorUserId))
                     .GroupBy(m => m.AuthorUserId!).Select(g => new { g.Key, B = g.Sum(m => m.Text.Length) }).ToListAsync())
        { For(g.Key).MessageBytes = g.B; }

        foreach (var p in await _db.StyleProfiles.AsNoTracking().Where(p => ids.Contains(p.UserId))
                     .Select(p => new { p.UserId, B = p.SummaryJson == null ? 0 : p.SummaryJson.Length }).ToListAsync())
        { For(p.UserId).ProfileBytes += p.B; }

        foreach (var p in await _db.PlayerStats.AsNoTracking().Where(p => ids.Contains(p.UserId))
                     .Select(p => new { p.UserId, p.GamesPlayed }).ToListAsync())
        { For(p.UserId).Games = p.GamesPlayed; }

        return t;
    }

    // ---- cleanup (phase 30) ----
    // POST /api/admin/cleanup { dryRun, confirm } — freshen the app without touching anything a
    // real player would miss:
    //   safeDelete   guests that never played, have no samples and haven't been seen in 24h
    //   staleGuests  the daily retention rule, run now (guests idle 30+ days)
    //   oldRewards   consumed rewards older than 90 days
    //   deadLobbies  in-memory lobbies with no human attached (ended, or empty 10+ min)
    // dryRun=true only counts. A real run needs confirm == "CLEANUP".
    [HttpPost("cleanup")]
    public async Task<IActionResult> Cleanup([FromBody] CleanupRequest req)
    {
        var dry = req?.DryRun ?? true;
        if (!dry && !string.Equals(req?.Confirm, CleanupConfirmPhrase, StringComparison.Ordinal))
            return BadRequest(new { error = $"type {CleanupConfirmPhrase} to confirm" });

        var now = DateTime.UtcNow;

        // safe-delete candidates
        var guestIds = await _db.Users.AsNoTracking()
            .Where(u => u.IsGuest && (u.LastSeenUtc == null || u.LastSeenUtc < now.AddHours(-24)))
            .Select(u => u.Id).ToListAsync();
        var withGames = await _db.PlayerStats.AsNoTracking().Where(p => guestIds.Contains(p.UserId) && p.GamesPlayed > 0)
            .Select(p => p.UserId).ToListAsync();
        var withSamples = await _db.WritingSamples.AsNoTracking().Where(s => guestIds.Contains(s.UserId))
            .Select(s => s.UserId).Distinct().ToListAsync();
        var safeIds = guestIds.Except(withGames).Except(withSamples).ToList();

        // stale guests per retention (excluding the safe set so we don't double count)
        var retentionDays = _config.GetValue<int?>("Retention:GuestDays") ?? 30;
        var staleIds = await _db.Users.AsNoTracking()
            .Where(u => u.IsGuest && u.LastSeenUtc != null && u.LastSeenUtc < now.AddDays(-retentionDays))
            .Select(u => u.Id).ToListAsync();
        staleIds = staleIds.Except(safeIds).ToList();

        var oldRewards = await _db.UserRewards.Where(r => r.ConsumedAt != null && r.ConsumedAt < now.AddDays(-90)).ToListAsync();

        var deadLobbies = new List<string>();
        foreach (var l in _lobbies.All)
        {
            lock (l.Sync)
            {
                if (l.IsSelfCheck) continue;
                var anyHuman = l.Players.Any(p => !p.IsBot && p.IsConnected);
                if (!anyHuman) deadLobbies.Add(l.Code);
            }
        }

        var report = new
        {
            dryRun = dry,
            safeDelete = safeIds.Count,
            staleGuests = staleIds.Count,
            oldRewards = oldRewards.Count,
            deadLobbies = deadLobbies.Count,
        };
        if (dry) return Ok(report);

        var removedUsers = await GameApi.Retention.GuestRetentionService.PurgeUsersAsync(_db, safeIds.Concat(staleIds).ToList());
        _db.UserRewards.RemoveRange(oldRewards);
        await _db.SaveChangesAsync();
        var removedLobbies = deadLobbies.Count(code => _lobbies.Remove(code));

        _logger.LogWarning("Admin cleanup: {Users} accounts, {Rewards} rewards, {Lobbies} lobbies removed",
            removedUsers, oldRewards.Count, removedLobbies);
        return Ok(new { report.dryRun, report.safeDelete, report.staleGuests, report.oldRewards, deadLobbies = removedLobbies, removedUsers });
    }

    private const string CleanupConfirmPhrase = "CLEANUP";

    // ---- self-check (phase 30) ----
    // POST /api/admin/selfcheck — start a run (409 if one is going). GET — the current run.
    [HttpPost("selfcheck")]
    public IActionResult StartSelfCheck()
    {
        if (!_selfCheck.Start()) return Conflict(new { error = "a self-check is already running" });
        return Ok(_selfCheck.Snapshot());
    }

    [HttpGet("selfcheck")]
    public IActionResult SelfCheck() => Ok(_selfCheck.Snapshot() ?? new SelfCheckService.Run("", DateTime.MinValue, null, false, new()));

    // ---- operator cheats (phase 30) ----
    [HttpGet("cheats")]
    public IActionResult Cheats()
    {
        var (reveal, tokens) = _cheats.Snapshot();
        return Ok(new { revealAi = reveal, infiniteTokens = tokens });
    }

    [HttpPost("cheats")]
    public IActionResult SetCheats([FromBody] CheatsRequest req)
    {
        _cheats.Set(req?.RevealAi, req?.InfiniteTokens);
        var (reveal, tokens) = _cheats.Snapshot();
        _logger.LogWarning("Admin cheats: revealAi={Reveal} infiniteTokens={Tokens}", reveal, tokens);
        return Ok(new { revealAi = reveal, infiniteTokens = tokens });
    }

    // GET /api/admin/users/{id} — the per-user synopsis behind a clicked row. Everything the
    // operator needs to understand an account at a glance; deliberately NOT the raw sample
    // TEXT (that's private) — just counts + total characters so the row can say "12 samples,
    // 3.4k chars" without exposing what someone wrote.
    [HttpGet("users/{id}")]
    public async Task<IActionResult> UserProfile(string id)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound(new { error = "no such user" });

        var stats = await _db.PlayerStats.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == id);
        var sampleCount = await _db.WritingSamples.CountAsync(s => s.UserId == id);
        var sampleChars = await _db.WritingSamples.Where(s => s.UserId == id).SumAsync(s => (long?)s.Text.Length) ?? 0;
        var rewards = await _db.UserRewards.AsNoTracking().Where(r => r.UserId == id).ToListAsync();

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
    public record CleanupRequest(bool? DryRun, string? Confirm);
    public record CheatsRequest(bool? RevealAi, bool? InfiniteTokens);
}
