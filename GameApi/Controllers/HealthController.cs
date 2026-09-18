using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GameApi.Data;
using GameApi.HealthChecks;

namespace GameApi.Controllers;

[Route("api/[controller]")]
[ApiController]
public class HealthController : ControllerBase
{
    private readonly DatabaseHealthCheck _dbCheck;
    private readonly StorageMode _storage;

    public HealthController(DatabaseHealthCheck dbCheck, StorageMode storage)
    {
        _dbCheck = dbCheck;
        _storage = storage;
    }

    // GET /api/health — always 200 so UptimeRobot stays green; db flag reports reachability.
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        // In memory mode the EF ping "succeeds" against the fallback store, which would
        // hide the outage from the keepalive — report db:false until Postgres is really back.
        var dbUp = !_storage.IsMemory && await _dbCheck.IsDatabaseReachableAsync(cancellationToken);
        return Ok(new { status = "ok", db = dbUp, storage = _storage.Current });
    }
}
