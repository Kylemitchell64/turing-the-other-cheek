using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GameApi.Admin;
using GameApi.Data;

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
    private readonly StorageMode _storage;

    public StatusController(MaintenanceState maintenance, StorageMode storage)
    {
        _maintenance = maintenance;
        _storage = storage;
    }

    // GET /api/status -> { maintenance: bool, message: string?, storage: "postgres"|"memory" }
    // storage=memory means Postgres was down at boot and progress won't be saved (see
    // StorageMode); the client shows a soft notice so players aren't surprised.
    [HttpGet]
    public IActionResult Get()
    {
        var (on, message) = _maintenance.Snapshot();
        return Ok(new { maintenance = on, message, storage = _storage.Current });
    }
}
