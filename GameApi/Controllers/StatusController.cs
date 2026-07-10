using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GameApi.Admin;

namespace GameApi.Controllers;

// Public, anonymous status probe so the client can banner a maintenance pause without
// needing auth. Returns the current maintenance flag + operator message. Deliberately
// leaks nothing else.
[Route("api/status")]
[ApiController]
[AllowAnonymous]
public class StatusController : ControllerBase
{
    private readonly MaintenanceState _maintenance;

    public StatusController(MaintenanceState maintenance) => _maintenance = maintenance;

    // GET /api/status -> { maintenance: bool, message: string? }
    [HttpGet]
    public IActionResult Get()
    {
        var (on, message) = _maintenance.Snapshot();
        return Ok(new { maintenance = on, message });
    }
}
