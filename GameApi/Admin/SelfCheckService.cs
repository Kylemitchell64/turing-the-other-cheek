using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using GameApi.Characters;
using GameApi.Data;
using GameApi.GameLoop;
using GameApi.Lobbies;
using GameApi.Models;

namespace GameApi.Admin;

// Admin self-check (phase 30): "is everything actually working?" answered by doing it, not
// by reading config. One run at a time, steps in order, results polled by the console:
//   1. database   — SELECT 1 against the live store, which store we booted on, row counts
//   2. migrations — no pending EF migrations (relational stores only)
//   3. config     — JWT key length, CORS origins, which AI legs have keys, breaker states
//   4. ai ping    — one tiny real completion through the failover chain (which leg answered)
//   5. game chain — a synthetic solo game on fast windows: bots seat, everyone answers, the
//                   AI answers, the scripted accusation + fake-out fires, the game ends.
//                   Never persisted, removed from the store afterwards.
//   6. lobby store — live lobby count, and how many the dead-lobby sweep will reap
// Each step is pass / warn / fail with a one-line detail and its duration.
public sealed class SelfCheckService
{
    public sealed record Step(string Key, string Label, string Status, string Detail, long Ms);
    public sealed record Run(string Id, DateTime StartedUtc, DateTime? FinishedUtc, bool Running, List<Step> Steps);

    private readonly IServiceScopeFactory _scopes;
    private readonly StorageMode _storage;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;
    private readonly AiProviderStats _aiStats;
    private readonly IAiTextProvider _ai;
    private readonly LobbyStore _store;
    private readonly GameEngine _engine;
    private readonly ILogger<SelfCheckService> _logger;

    private readonly object _sync = new();
    private Run? _current;
    private List<Step> _steps = new();

    public SelfCheckService(
        IServiceScopeFactory scopes, StorageMode storage, IConfiguration config, IHostEnvironment env,
        AiProviderStats aiStats, IAiTextProvider ai, LobbyStore store, GameEngine engine,
        ILogger<SelfCheckService> logger)
    {
        _scopes = scopes; _storage = storage; _config = config; _env = env;
        _aiStats = aiStats; _ai = ai; _store = store; _engine = engine; _logger = logger;
    }

    public Run? Snapshot()
    {
        lock (_sync)
        {
            return _current == null ? null : _current with { Steps = _steps.ToList() };
        }
    }

    // Kick off a run in the background. Returns false if one is already going.
    public bool Start()
    {
        lock (_sync)
        {
            if (_current is { Running: true }) return false;
            _steps = new List<Step>();
            _current = new Run(Guid.NewGuid().ToString("N")[..8], DateTime.UtcNow, null, true, _steps);
        }
        _ = Task.Run(RunAllAsync);
        return true;
    }

    private void Report(Step step)
    {
        lock (_sync)
        {
            var i = _steps.FindIndex(s => s.Key == step.Key);
            if (i >= 0) _steps[i] = step; else _steps.Add(step);
        }
    }

    private async Task Timed(string key, string label, Func<Task<(string status, string detail)>> body)
    {
        Report(new Step(key, label, "running", "", 0));
        var sw = Stopwatch.StartNew();
        try
        {
            var (status, detail) = await body();
            Report(new Step(key, label, status, detail, sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "self-check step {Key} threw", key);
            Report(new Step(key, label, "fail", ex.GetType().Name + ": " + ex.Message, sw.ElapsedMilliseconds));
        }
    }

    private async Task RunAllAsync()
    {
        try
        {
            await Timed("db", "database", CheckDatabaseAsync);
            await Timed("migrations", "migrations", CheckMigrationsAsync);
            await Timed("config", "config + ai keys", CheckConfigAsync);
            await Timed("ai", "ai provider ping", CheckAiAsync);
            await Timed("game", "full game chain (synthetic solo)", CheckGameChainAsync);
            await Timed("lobbies", "lobby store", CheckLobbiesAsync);
            await Timed("freetier", "free-tier headroom", CheckFreeTierAsync);
        }
        finally
        {
            lock (_sync)
            {
                if (_current != null) _current = _current with { Running = false, FinishedUtc = DateTime.UtcNow };
            }
        }
    }

    private async Task<(string, string)> CheckDatabaseAsync()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        if (db.Database.IsRelational())
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cts.Token);
        var users = await db.Users.CountAsync(cts.Token);
        var games = await db.Games.CountAsync(cts.Token);
        var status = _storage.IsMemory ? "warn" : "pass";
        return (status, $"store={_storage.Current}, users={users}, games={games}" + (_storage.IsMemory ? " — running on the in-memory fallback, nothing persists" : ""));
    }

    private async Task<(string, string)> CheckMigrationsAsync()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        if (!db.Database.IsRelational()) return ("pass", "in-memory store, no migrations to apply");
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        return pending.Count == 0
            ? ("pass", "schema is current")
            : ("fail", $"{pending.Count} pending: {string.Join(", ", pending.Take(3))}");
    }

    private Task<(string, string)> CheckConfigAsync()
    {
        var notes = new List<string>();
        var status = "pass";

        var jwt = Environment.GetEnvironmentVariable("JWT_KEY") ?? _config["Jwt:Key"] ?? "";
        if (jwt.Length < 64) { status = "fail"; notes.Add($"JWT key is {jwt.Length} chars (<64)"); }
        else notes.Add($"jwt key {jwt.Length} chars");

        var origins = _config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        if (_env.IsProduction() && origins.All(o => o.Contains("localhost")))
        {
            status = status == "fail" ? "fail" : "warn";
            notes.Add("CORS only allows localhost in production");
        }
        else notes.Add($"cors: {(origins.Length == 0 ? "default localhost" : string.Join(" ", origins))}");

        var legs = _aiStats.Snapshot(DateTime.UtcNow).ToList();
        if (!_ai.HasKey)
        {
            status = status == "fail" ? "fail" : "warn";
            notes.Add("no AI key on any leg — the Mock brain is playing");
        }
        foreach (var p in legs)
        {
            var state = p.BreakerOpen ? "breaker open" : p.ExhaustedForDay ? "quota spent" : "ok";
            notes.Add($"{p.Provider}: {state}, {p.RequestsToday} req today");
        }
        return Task.FromResult((status, string.Join(" · ", notes)));
    }

    private async Task<(string, string)> CheckAiAsync()
    {
        if (!_ai.HasKey) return ("warn", "skipped — no provider key configured (Mock brain)");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var before = _aiStats.Snapshot(DateTime.UtcNow).ToDictionary(p => p.Provider, p => p.RequestsToday);
        var text = await _ai.GenerateAsync(
            "You are a health probe. Reply with exactly the two letters OK and nothing else.",
            "ping", 0.0, 5, cts.Token);
        var after = _aiStats.Snapshot(DateTime.UtcNow);
        var leg = after.FirstOrDefault(p => !before.TryGetValue(p.Provider, out var b) || p.RequestsToday > b)?.Provider ?? "?";
        if (string.IsNullOrWhiteSpace(text)) return ("fail", $"no completion from any leg (tried via {leg})");
        var ok = text.Trim().ToUpperInvariant().StartsWith("OK");
        return (ok ? "pass" : "warn", $"{leg} answered \"{text.Trim()[..Math.Min(text.Trim().Length, 24)]}\"");
    }

    private async Task<(string, string)> CheckGameChainAsync()
    {
        var lobby = _store.Create("selfcheck:" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var outbound = new List<Func<Task>>();
            lock (lobby.Sync)
            {
                lobby.IsSolo = true;
                lobby.IsSelfCheck = true;
                lobby.Mode = GameModes.Classic;
                var host = new LobbyPlayer { UserId = lobby.HostUserId, DisplayName = "Inspector" };
                lobby.Players.Add(host);
                var taken = new List<string> { host.DisplayName };
                for (var i = 0; i < 3; i++)
                {
                    var name = _store.PickAiName(taken);
                    taken.Add(name);
                    lobby.Players.Add(new LobbyPlayer { UserId = "bot:" + Guid.NewGuid().ToString("N"), DisplayName = name, IsBot = true });
                }
                lobby.AiDisplayName = _store.PickAiName(taken);
                _engine.BeginGame(lobby, outbound);
            }
            foreach (var send in outbound) await send();

            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                lock (lobby.Sync) { if (lobby.State == GameState.Ended) break; }
                await Task.Delay(250);
            }

            string status, detail;
            lock (lobby.Sync)
            {
                if (lobby.State != GameState.Ended)
                    return ("fail", $"game did not finish in 90s (stuck in {lobby.State}, round {lobby.RoundNumber})");

                var rounds = lobby.RoundPrompts.Count;
                var aiLines = lobby.Transcript.Where(r => r.IsAi).ToList();
                var aiBlank = aiLines.Count(r => r.Text == "(no answer)");
                var botBlank = lobby.Transcript.Count(r => !r.IsAi && r.DisplayName != "Inspector" && r.Text == "(no answer)");
                var vetoed = lobby.AccusationLog.Count(a => a.Outcome == "vetoed");
                var leaked = lobby.AccusationLog.Count(a => a.Outcome != "vetoed");
                var fallbacks = lobby.FallbackState.Count;

                status = "pass";
                var notes = new List<string> { $"{rounds} rounds", $"ended {lobby.WinType}" };
                if (aiBlank > 0) { status = "warn"; notes.Add($"AI missed {aiBlank} answer window(s)"); }
                else notes.Add($"AI answered {aiLines.Count}/{rounds}" + (fallbacks > 0 ? $" ({fallbacks} canned fallback)" : ""));
                if (botBlank > 0) { status = "fail"; notes.Add($"bots missed {botBlank} answers"); }
                if (vetoed == 0) { status = status == "fail" ? "fail" : "warn"; notes.Add("scripted accusation/fake-out did not fire"); }
                else notes.Add($"{vetoed} accusation(s) faked-out");
                if (leaked > 0) { status = "fail"; notes.Add($"{leaked} accusation(s) resolved unvetoed in a self-check"); }
                detail = string.Join(" · ", notes);
            }
            return (status, detail);
        }
        finally
        {
            _store.Remove(lobby.Code);
        }
    }

    // Uptime is the #1 rule: every free cap we sit under, with how much is left. Warn at
    // 75%, fail at 90%. Same estimates the FREE TIER tile uses.
    private async Task<(string, string)> CheckFreeTierAsync()
    {
        var now = DateTime.UtcNow;
        var notes = new List<string>();
        var worst = 0.0;
        void Add(string label, double used, double limit, string unit)
        {
            var pct = limit <= 0 ? 0 : used / limit * 100;
            worst = Math.Max(worst, pct);
            notes.Add($"{label} {Math.Round(used, 1)}/{limit}{unit} ({Math.Round(pct)}%)");
        }
        foreach (var p in _aiStats.Snapshot(now))
        {
            var cap = _config.GetValue<int?>($"FreeTier:Caps:{p.Provider}") ?? (p.Provider.ToLowerInvariant() == "gemini" ? 1500 : 14400);
            Add(p.Provider, p.RequestsToday, cap, " req");
        }
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        var sampleBytes = await db.WritingSamples.SumAsync(s => (long?)s.Text.Length) ?? 0;
        var messageBytes = await db.GameMessages.SumAsync(m => (long?)m.Text.Length) ?? 0;
        var userCount = await db.Users.CountAsync();
        Add("supabase", (sampleBytes + messageBytes + userCount * 512L) / (1024.0 * 1024.0), _config.GetValue<int?>("FreeTier:SupabaseMb") ?? 500, "MB");
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        Add("render", (now - monthStart).TotalHours, _config.GetValue<int?>("FreeTier:RenderHours") ?? 750, "h");
        var status = worst >= 90 ? "fail" : worst >= 75 ? "warn" : "pass";
        return (status, string.Join(" · ", notes));
    }

    private Task<(string, string)> CheckLobbiesAsync()
    {
        var all = _store.All.ToList();
        var live = 0; var empty = 0;
        foreach (var l in all)
        {
            lock (l.Sync)
            {
                if (l.Players.Any(p => !p.IsBot && p.IsConnected)) live++; else empty++;
            }
        }
        return Task.FromResult(("pass", $"{live} live lobbies, {empty} empty (sweep reaps these within 10 min, ended ones at once)"));
    }
}
