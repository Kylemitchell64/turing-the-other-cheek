using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GameApi.Admin;
using GameApi.Auth;
using GameApi.Characters;
using GameApi.Data;
using GameApi.Dtos;
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
    // Same rule as guest names: 3-20 chars, letters/digits/underscore.
    private static readonly Regex UsernameRegex = new("^[A-Za-z0-9_]{3,20}$", RegexOptions.Compiled);

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly GameContext _db;
    private readonly JwtTokenService _tokens;
    private readonly ILogger<ProfileController> _logger;

    public ProfileController(
        UserManager<ApplicationUser> userManager,
        GameContext db,
        JwtTokenService tokens,
        ILogger<ProfileController> logger)
    {
        _userManager = userManager;
        _db = db;
        _tokens = tokens;
        _logger = logger;
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

    // GET /api/profile/rewards — the signed-in player's held rewards, in the shape the
    // character creator reads: which premium outfit/accessory ids they've unlocked, and how
    // many unconsumed cheat cards they hold. Nothing here is identity-sensitive.
    [HttpGet("rewards")]
    public async Task<IActionResult> GetRewards()
    {
        var rewards = await _db.UserRewards
            .AsNoTracking()
            .Where(r => r.UserId == UserId)
            .ToListAsync();

        var outfits = new SortedSet<int>();
        var accessories = new SortedSet<int>();
        var cheatCards = 0;
        foreach (var r in rewards)
        {
            if (r.Kind == RewardKinds.CheatCard)
            {
                if (r.ConsumedAt == null) cheatCards++;
            }
            else if (RewardKinds.TryOutfit(r.Kind, out var o)) outfits.Add(o);
            else if (RewardKinds.TryAccessory(r.Kind, out var a)) accessories.Add(a);
        }

        return Ok(new
        {
            unlockedOutfits = outfits.ToArray(),
            unlockedAccessories = accessories.ToArray(),
            cheatCards
        });
    }

    // PUT /api/profile/character — save this player's character. Validates known keys
    // only, each index inside the sprite system's real ranges; rejects anything else.
    // Premium outfit/accessory ids (above the free set) additionally require the player to
    // hold the matching unlock reward — so a client can't save a locked look it wasn't granted.
    [HttpPut("character")]
    public async Task<IActionResult> PutCharacter([FromBody] JsonElement body)
    {
        if (!CharacterDefaults.TryParse(body, out var cfg, out var error) || cfg == null)
            return BadRequest(new { error = error ?? "invalid character" });

        // Reward gate: anything above the free set must be unlocked for this user.
        if (!CharacterDefaults.IsOutfitFree(cfg.Outfit) || !CharacterDefaults.IsAccessoryFree(cfg.Accessory))
        {
            var kinds = await _db.UserRewards
                .Where(r => r.UserId == UserId)
                .Select(r => r.Kind)
                .ToListAsync();
            var unlockedOutfits = kinds.Select(k => RewardKinds.TryOutfit(k, out var o) ? o : -1).ToHashSet();
            var unlockedAccessories = kinds.Select(k => RewardKinds.TryAccessory(k, out var a) ? a : -1).ToHashSet();

            if (!CharacterDefaults.IsOutfitFree(cfg.Outfit) && !unlockedOutfits.Contains(cfg.Outfit))
                return BadRequest(new { error = "that outfit is locked — earn it as a reward" });
            if (!CharacterDefaults.IsAccessoryFree(cfg.Accessory) &&
                cfg.Accessory is int accId && !unlockedAccessories.Contains(accId))
                return BadRequest(new { error = "that accessory is locked — earn it as a reward" });
        }

        var user = await _userManager.FindByIdAsync(UserId);
        if (user == null) return NotFound();

        user.CharacterJson = CharacterDefaults.ToJson(cfg);
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => e.Description) });

        return Ok(cfg);
    }

    // POST /api/profile/username — the signed-in user (typically a fresh OAuth account
    // flagged NeedsUsername) claims a display name. Three branches:
    //   * name is free                  -> take it.
    //   * name belongs to a GUEST       -> MERGE that guest's data into this account
    //                                      (character/profile/samples/stats), delete the
    //                                      guest, then take the name — "claiming" a prior
    //                                      guest identity so the player keeps their history.
    //   * name belongs to a NON-guest   -> 409, it's someone else's account.
    // Returns a fresh AuthResponse (the display name + needsUsername flag changed).
    [HttpPost("username")]
    public async Task<IActionResult> ChooseUsername([FromBody] ChooseUsernameRequest req)
    {
        var name = (req?.Username ?? "").Trim();
        if (!UsernameRegex.IsMatch(name))
            return BadRequest(new { error = "username must be 3-20 letters, numbers or underscores" });

        var me = await _userManager.FindByIdAsync(UserId);
        if (me == null) return NotFound();

        var owner = await _userManager.FindByNameAsync(name);
        if (owner != null && owner.Id != me.Id)
        {
            if (!owner.IsGuest)
                return Conflict(new { error = "that name is taken" });

            // Claim the guest identity: fold its data in, then remove the guest row so the
            // name frees up for the rename below.
            await MergeGuestIntoAsync(owner, me);
            var delGuest = await _userManager.DeleteAsync(owner);
            if (!delGuest.Succeeded)
                return BadRequest(new { errors = delGuest.Errors.Select(e => e.Description) });
        }

        // Take the name (covers free-name and the just-merged case). SetUserNameAsync
        // updates the normalized username too; it persists the other tracked changes with it.
        me.DisplayName = name;
        me.NeedsUsername = false;
        me.LastSeenUtc = DateTime.UtcNow;
        var setName = await _userManager.SetUserNameAsync(me, name);
        if (!setName.Succeeded)
            return BadRequest(new { errors = setName.Errors.Select(e => e.Description) });

        _logger.LogInformation("User {Id} claimed username {Name}", me.Id, name);
        return Ok(_tokens.BuildAuthResponse(me));
    }

    // Move a guest account's data onto the target account. Everything is re-pointed or
    // summed rather than dropped, so the player keeps what they built as a guest. Runs on
    // the request-scoped GameContext (the same instance UserManager uses).
    private async Task MergeGuestIntoAsync(ApplicationUser guest, ApplicationUser target)
    {
        // Character: only if the target hasn't made one (don't clobber a chosen look).
        if (string.IsNullOrEmpty(target.CharacterJson) && !string.IsNullOrEmpty(guest.CharacterJson))
            target.CharacterJson = guest.CharacterJson;

        // Style profile: re-point the guest's if the target has none, else keep target's.
        var guestProfile = await _db.StyleProfiles.FirstOrDefaultAsync(p => p.UserId == guest.Id);
        if (guestProfile != null)
        {
            var targetProfile = await _db.StyleProfiles.FirstOrDefaultAsync(p => p.UserId == target.Id);
            if (targetProfile == null) guestProfile.UserId = target.Id;
            else _db.StyleProfiles.Remove(guestProfile);
        }

        // Writing samples: all move to the target.
        foreach (var s in await _db.WritingSamples.Where(s => s.UserId == guest.Id).ToListAsync())
            s.UserId = target.Id;

        // Player stats: sum numeric fields into the target's row (or re-point if none).
        var guestStats = await _db.PlayerStats.FirstOrDefaultAsync(s => s.UserId == guest.Id);
        if (guestStats != null)
        {
            var targetStats = await _db.PlayerStats.FirstOrDefaultAsync(s => s.UserId == target.Id);
            if (targetStats == null)
            {
                guestStats.UserId = target.Id;
            }
            else
            {
                targetStats.DetectorWins += guestStats.DetectorWins;
                targetStats.GamesPlayed += guestStats.GamesPlayed;
                targetStats.TimesFooled += guestStats.TimesFooled;
                targetStats.AiSurvivalGamesWitnessed += guestStats.AiSurvivalGamesWitnessed;
                _db.PlayerStats.Remove(guestStats);
            }
        }

        // Game history: re-point so it survives under the claimed identity AND so the
        // guest's GamePlayer rows (non-nullable, Restrict FK) don't block its deletion.
        foreach (var gp in await _db.GamePlayers.Where(p => p.UserId == guest.Id).ToListAsync())
            gp.UserId = target.Id;
        foreach (var gm in await _db.GameMessages.Where(m => m.AuthorUserId == guest.Id).ToListAsync())
            gm.AuthorUserId = target.Id;

        await _db.SaveChangesAsync();

        // The target is a non-guest account, so trim its merged samples to the 200 cap.
        await SampleCaps.EnforceAsync(_db, target.Id, isGuest: false);
    }
}
