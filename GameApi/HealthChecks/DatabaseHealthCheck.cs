using Microsoft.EntityFrameworkCore;
using GameApi.Data;

namespace GameApi.HealthChecks;

// Pings the DB with SELECT 1. The /api/health endpoint stays 200 even when this fails
// (it just reports db:false) so UptimeRobot pings keep hitting a live endpoint.
public class DatabaseHealthCheck
{
    private readonly GameContext _context;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    public DatabaseHealthCheck(GameContext context, ILogger<DatabaseHealthCheck> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<bool> IsDatabaseReachableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            await _context.Database.ExecuteSqlRawAsync("SELECT 1", linkedCts.Token);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database health check failed");
            return false;
        }
    }
}
