using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GameApi.HealthChecks;

namespace GameApi.Controllers;

[Route("api/[controller]")]
[ApiController]
public class HealthController : ControllerBase
{
    private readonly DatabaseHealthCheck _dbCheck;

    public HealthController(DatabaseHealthCheck dbCheck)
    {
        _dbCheck = dbCheck;
    }

    // GET /api/health — always 200 so UptimeRobot stays green; db flag reports reachability.
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var dbUp = await _dbCheck.IsDatabaseReachableAsync(cancellationToken);
        return Ok(new { status = "ok", db = dbUp });
    }
}
