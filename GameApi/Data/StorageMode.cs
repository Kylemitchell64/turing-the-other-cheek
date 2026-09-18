using Npgsql;
using GameApi.Lobbies;
using GameApi.Models;

namespace GameApi.Data;

// Which store the process actually booted on. Normally Postgres. When Postgres was
// unreachable at startup (Supabase free tier pauses a project after 7 idle days, and the
// first request after that fails hard) the API falls back to EF InMemory so the game stays
// playable — guest login, lobbies, the AI, everything — with the one caveat that nothing
// persists past a restart. /api/status + /api/health expose this so the client can say so.
public sealed class StorageMode
{
    public const string Postgres = "postgres";
    public const string Memory = "memory";

    public string Current { get; }
    public bool IsMemory => Current == Memory;

    public StorageMode(string current) => Current = current;

    // Cheap reachability probe with a hard timeout, independent of EF so it can run before
    // the service container exists. Any exception (DNS, refused, auth, paused pooler) is
    // "unreachable" — the caller decides what to do about it.
    public static bool ProbePostgres(string connectionString, TimeSpan timeout)
    {
        try
        {
            var csb = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Timeout = (int)Math.Max(1, timeout.TotalSeconds),
                CommandTimeout = (int)Math.Max(1, timeout.TotalSeconds),
            };
            using var conn = new NpgsqlConnection(csb.ConnectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT 1", conn);
            cmd.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

// Runs only when the process booted in memory mode. Every few minutes it re-probes
// Postgres; once it's back AND no lobby is mid-game, it stops the host. Render (and any
// container platform) restarts an exited web service, and the fresh process comes up on
// Postgres. Net effect: unpause Supabase, and within a few minutes the live game is
// persistent again with no manual redeploy — and never mid-round.
public sealed class PostgresRecoveryWatcher : BackgroundService
{
    private readonly string _connectionString;
    private readonly LobbyStore _lobbies;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PostgresRecoveryWatcher> _logger;
    private readonly TimeSpan _interval;

    public PostgresRecoveryWatcher(
        string connectionString,
        LobbyStore lobbies,
        IHostApplicationLifetime lifetime,
        IConfiguration config,
        ILogger<PostgresRecoveryWatcher> logger)
    {
        _connectionString = connectionString;
        _lobbies = lobbies;
        _lifetime = lifetime;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(config.GetValue<int?>("Db:RecoveryProbeSeconds") ?? 180);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogWarning(
            "Booted on the in-memory store because Postgres was unreachable. Probing every {Secs}s; will restart onto Postgres once it answers and no game is in progress.",
            (int)_interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            if (!StorageMode.ProbePostgres(_connectionString, TimeSpan.FromSeconds(5)))
                continue;

            var busy = _lobbies.All.Any(l => l.State != GameState.Lobby && l.State != GameState.Ended);
            if (busy)
            {
                _logger.LogInformation("Postgres is back but a game is in progress; will retry after it ends.");
                continue;
            }

            _logger.LogWarning("Postgres is reachable again — stopping so the host restarts onto persistent storage.");
            _lifetime.StopApplication();
            return;
        }
    }
}
