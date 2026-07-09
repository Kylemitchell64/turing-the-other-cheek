using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using GameApi.Characters;
using GameApi.Models;

namespace GameApi.Controllers;

// The signed-in player's saved character (phase 12 creator). Works the same for guests
// and OAuth/password users — it's stored on the Identity user, and guests resolve to the
// same account by username, so the same guest name later keeps the same character.
[Route("api/profile")]
[ApiController]
[Authorize]
public class ProfileController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;

    public ProfileController(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    private string UserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("No user id on token");

    // GET /api/profile/character -> the saved config, or null if the player never saved
    // one (the client then offers the first-use creator).
    [HttpGet("character")]
    public async Task<IActionResult> GetCharacter()
    {
        var user = await _userManager.FindByIdAsync(UserId);
        if (user == null) return NotFound();

        if (string.IsNullOrEmpty(user.CharacterJson) ||
            !CharacterDefaults.TryParse(user.CharacterJson, out var cfg, out _) || cfg == null)
            return new JsonResult(null); // Ok(null) would send an empty body, not JSON null

        return new JsonResult(cfg);
    }

    // PUT /api/profile/character — save this player's character. Validates known keys
    // only, each index inside the sprite system's real ranges; rejects anything else.
    [HttpPut("character")]
    public async Task<IActionResult> PutCharacter([FromBody] JsonElement body)
    {
        if (!CharacterDefaults.TryParse(body, out var cfg, out var error) || cfg == null)
            return BadRequest(new { error = error ?? "invalid character" });

        var user = await _userManager.FindByIdAsync(UserId);
        if (user == null) return NotFound();

        user.CharacterJson = CharacterDefaults.ToJson(cfg);
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => e.Description) });

        return Ok(cfg);
    }
}
