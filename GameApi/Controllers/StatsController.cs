using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GameApi.Data;

namespace GameApi.Controllers;

// Career stats for the signed-in player. Rows are written/updated in the game-end
// persistence path (GameEngine). The My Stats screen renders these four counters.
[Route("api/stats")]
[ApiController]
[Authorize]
public class StatsController : ControllerBase
{
    private readonly GameContext _db;

    public StatsController(GameContext db)
    {
        _db = db;
    }

    public record StatsDto(int DetectorWins, int GamesPlayed, int TimesFooled, int AiSurvivalGamesWitnessed);

    // GET /api/stats/me — this user's record (all zeros if they've never finished a game).
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var stats = await _db.PlayerStats.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId);

        return Ok(new StatsDto(
            DetectorWins: stats?.DetectorWins ?? 0,
            GamesPlayed: stats?.GamesPlayed ?? 0,
            TimesFooled: stats?.TimesFooled ?? 0,
            AiSurvivalGamesWitnessed: stats?.AiSurvivalGamesWitnessed ?? 0));
    }
}
