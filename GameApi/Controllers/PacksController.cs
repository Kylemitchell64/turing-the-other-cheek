using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using GameApi.GameLoop;

namespace GameApi.Controllers;

// The AI category maker (phase 20). Two endpoints, both authed:
//   POST /api/packs/generate {theme, count} -> AI-built pack behind safety guardrails
//   POST /api/packs/decode   {code}         -> verify + unpack a signed share-code
// Neither stores anything server-side: a generated pack is returned to the client, which
// carries it in a signed code and hands it back to a lobby via the hub.
[Route("api/packs")]
[ApiController]
[Authorize]
public class PacksController : ControllerBase
{
    private readonly IAiTextProvider _ai;
    private readonly PackCodec _codec;
    private readonly PackGenRateLimiter _limiter;
    private readonly ILogger<PacksController> _logger;

    public PacksController(IAiTextProvider ai, PackCodec codec, PackGenRateLimiter limiter,
        ILogger<PacksController> logger)
    {
        _ai = ai;
        _codec = codec;
        _limiter = limiter;
        _logger = logger;
    }

    private string UserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("No user id on token");

    public record GenerateRequest(string? Theme, int? Count);
    public record DecodeRequest(string? Code);
    public record PackResponse(string Name, bool Nsfw, string[] Prompts, string Code);

    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] GenerateRequest req, CancellationToken ct)
    {
        var theme = (req?.Theme ?? "").Trim();
        if (theme.Length < PackGenerator.MinThemeLength || theme.Length > PackGenerator.MaxThemeLength)
            return BadRequest(new { error = $"theme needs to be {PackGenerator.MinThemeLength}-{PackGenerator.MaxThemeLength} characters" });

        // ~3 generations per minute per user (AI calls aren't free).
        if (!_limiter.TryTake(UserId))
            return StatusCode(429, new { error = "slow down a sec — you can make a few of these a minute" });

        var count = req?.Count ?? PackGenerator.DefaultCount;

        PackGenResult result;
        try
        {
            result = await PackGenerator.GenerateAsync(_ai, theme, count, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "pack generation threw for theme length {Len}", theme.Length);
            return StatusCode(422, new { error = "couldn't make that one, try a different theme" });
        }

        if (result.Outcome != PackGenOutcome.Ok || result.Pack == null)
        {
            _logger.LogInformation("pack generation {Outcome} for theme length {Len}", result.Outcome, theme.Length);
            return StatusCode(422, new { error = "couldn't make that one, try a different theme" });
        }

        var pack = result.Pack;
        var code = _codec.Encode(pack);
        return Ok(new PackResponse(pack.Name, pack.Nsfw, pack.Prompts, code));
    }

    [HttpPost("decode")]
    public IActionResult Decode([FromBody] DecodeRequest req)
    {
        var pack = _codec.TryDecode(req?.Code, out var error);
        if (pack == null)
            return BadRequest(new { error });

        var code = _codec.Encode(pack); // re-emit a canonical code
        return Ok(new PackResponse(pack.Name, pack.Nsfw, pack.Prompts, code));
    }
}

// Tiny per-user fixed-window limiter for the AI generate call — the global 30/min/IP
// limiter is per-IP, this caps a single account so one user can't burn the free AI tier.
public class PackGenRateLimiter
{
    private const int PerWindow = 3;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> _hits = new();

    public bool TryTake(string userId) => TryTake(userId, DateTime.UtcNow);

    // Clock-injectable for tests.
    public bool TryTake(string userId, DateTime now)
    {
        var updated = _hits.AddOrUpdate(
            userId,
            _ => (now, 1),
            (_, cur) => now - cur.WindowStart >= Window ? (now, 1) : (cur.WindowStart, cur.Count + 1));
        return updated.Count <= PerWindow;
    }
}
