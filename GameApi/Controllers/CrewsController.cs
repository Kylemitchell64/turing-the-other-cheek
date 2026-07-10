using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GameApi.Data;
using GameApi.Dtos;
using GameApi.Lobbies;
using GameApi.Models;

namespace GameApi.Controllers;

// Persistent groups ("crews") for signed-in (non-guest) players. A crew is a durable
// lobby they keep coming back to: a persistent join code, a saved pack/difficulty/pace,
// and a group profile the AI learns from how the group plays. Guests are refused with a
// clear "sign in" message — they have no durable identity to hang a crew on.
//
// Caps (protect the free DB): a user can OWN at most 3 crews and BELONG to at most 10.
[Route("api/crews")]
[ApiController]
[Authorize]
public class CrewsController : ControllerBase
{
    public const int MaxOwned = 3;
    public const int MaxMemberships = 10;
    private const int NameMin = 3;
    private const int NameMax = 24;

    private readonly GameContext _db;
    private readonly ILogger<CrewsController> _logger;

    public CrewsController(GameContext db, ILogger<CrewsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private string UserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("No user id on token");

    // The isGuest claim is baked into the JWT at login (JwtTokenService).
    private bool IsGuest =>
        string.Equals(User.FindFirstValue("isGuest"), "true", StringComparison.OrdinalIgnoreCase);

    // Guests get a 403 with a message the client shows as-is.
    private ObjectResult? GuestBlocked() =>
        IsGuest ? StatusCode(403, new { error = "sign in to create a crew" }) : null;

    // POST /api/crews  { name } -> create a crew with a fresh persistent code.
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCrewRequest req)
    {
        if (GuestBlocked() is { } blocked) return blocked;

        var name = (req?.Name ?? "").Trim();
        if (name.Length < NameMin || name.Length > NameMax)
            return BadRequest(new { error = $"crew name must be {NameMin}-{NameMax} characters" });

        var owned = await _db.Crews.CountAsync(c => c.OwnerUserId == UserId);
        if (owned >= MaxOwned)
            return BadRequest(new { error = $"you can own at most {MaxOwned} crews" });

        var crew = new Crew
        {
            Name = name,
            OwnerUserId = UserId,
            JoinCode = await MintUniqueCodeAsync(),
            CreatedAt = DateTime.UtcNow,
        };
        // The owner is the first member.
        crew.Members.Add(new CrewMember { UserId = UserId, JoinedAt = DateTime.UtcNow });

        _db.Crews.Add(crew);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Crew {Id} '{Name}' created by {User}", crew.Id, crew.Name, UserId);
        return Ok(ToDto(crew, memberCount: 1, isOwner: true));
    }

    // GET /api/crews/mine -> every crew I belong to, with member counts + saved config.
    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        if (GuestBlocked() is { } blocked) return blocked;

        var crews = await _db.CrewMembers
            .Where(m => m.UserId == UserId)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new
            {
                m.Crew!.Id,
                m.Crew.Name,
                m.Crew.JoinCode,
                m.Crew.PackKey,
                m.Crew.Difficulty,
                m.Crew.PaceKey,
                m.Crew.GamesPlayed,
                m.Crew.OwnerUserId,
                MemberCount = m.Crew.Members.Count,
            })
            .ToListAsync();

        var dtos = crews.Select(c => new CrewDto(
            c.Id, c.Name, c.JoinCode, c.PackKey, c.Difficulty, c.PaceKey,
            c.MemberCount, c.GamesPlayed, c.OwnerUserId == UserId)).ToList();

        return Ok(dtos);
    }

    // POST /api/crews/join  { code } -> join an existing crew by its persistent code.
    [HttpPost("join")]
    public async Task<IActionResult> Join([FromBody] JoinCrewRequest req)
    {
        if (GuestBlocked() is { } blocked) return blocked;

        var code = (req?.Code ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0)
            return BadRequest(new { error = "enter a crew code" });

        var crew = await _db.Crews
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.JoinCode == code);
        if (crew == null)
            return NotFound(new { error = "no crew with that code" });

        var isOwner = crew.OwnerUserId == UserId;
        if (crew.Members.Any(m => m.UserId == UserId))
            return Ok(ToDto(crew, crew.Members.Count, isOwner)); // already in — idempotent

        var memberships = await _db.CrewMembers.CountAsync(m => m.UserId == UserId);
        if (memberships >= MaxMemberships)
            return BadRequest(new { error = $"you're in the max of {MaxMemberships} crews" });

        crew.Members.Add(new CrewMember { CrewId = crew.Id, UserId = UserId, JoinedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        _logger.LogInformation("{User} joined crew {Id}", UserId, crew.Id);
        return Ok(ToDto(crew, crew.Members.Count, isOwner));
    }

    // DELETE /api/crews/{id}            -> leave. If the owner leaves, ownership transfers
    //                                      to the oldest remaining member; the last member
    //                                      out deletes the crew.
    // DELETE /api/crews/{id}?disband=true -> owner-only: delete the crew outright.
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Leave(int id, [FromQuery] bool disband = false)
    {
        if (GuestBlocked() is { } blocked) return blocked;

        var crew = await _db.Crews
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (crew == null) return NotFound(new { error = "no such crew" });

        var me = crew.Members.FirstOrDefault(m => m.UserId == UserId);
        if (me == null) return NotFound(new { error = "you're not in that crew" });

        if (disband)
        {
            if (crew.OwnerUserId != UserId)
                return StatusCode(403, new { error = "only the owner can disband the crew" });
            _db.Crews.Remove(crew); // cascades the members
            await _db.SaveChangesAsync();
            _logger.LogInformation("Crew {Id} disbanded by owner {User}", id, UserId);
            return Ok(new { disbanded = true });
        }

        _db.CrewMembers.Remove(me);

        var others = crew.Members.Where(m => m.UserId != UserId).ToList();
        if (others.Count == 0)
        {
            // Last member out — the crew goes with them.
            _db.Crews.Remove(crew);
            await _db.SaveChangesAsync();
            _logger.LogInformation("Crew {Id} deleted (last member left)", id);
            return Ok(new { deleted = true });
        }

        if (crew.OwnerUserId == UserId)
        {
            // Transfer ownership to the oldest remaining member.
            crew.OwnerUserId = others.OrderBy(m => m.JoinedAt).ThenBy(m => m.Id).First().UserId;
            _logger.LogInformation("Crew {Id} ownership transferred to {User}", id, crew.OwnerUserId);
        }

        await _db.SaveChangesAsync();
        return Ok(new { left = true });
    }

    // Mint a persistent join code not already taken by another crew. A different namespace
    // from live lobby codes — collisions between the two are fine (they resolve differently).
    private async Task<string> MintUniqueCodeAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            var code = LobbyStore.GenerateCode();
            if (!await _db.Crews.AnyAsync(c => c.JoinCode == code))
                return code;
        }
        // Astronomically unlikely with a 30^5 space and a free-tier crew count.
        throw new InvalidOperationException("could not mint a unique crew code");
    }

    private static CrewDto ToDto(Crew c, int memberCount, bool isOwner) => new(
        c.Id, c.Name, c.JoinCode, c.PackKey, c.Difficulty, c.PaceKey,
        memberCount, c.GamesPlayed, isOwner);
}
