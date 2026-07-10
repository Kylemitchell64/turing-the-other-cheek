using Microsoft.AspNetCore.Identity;

namespace GameApi.Models;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }

    // Guest accounts: created from just a username, no password. Same username later
    // resolves to the same account so the AI keeps learning that player's style.
    public bool IsGuest { get; set; }

    // OAuth accounts: which provider ("Google" | "GitHub") and that provider's stable
    // user id. Null for password + guest accounts. Together they find-or-create on login.
    public string? ExternalProvider { get; set; }
    public string? ExternalId { get; set; }

    // The player's saved character (phase 12 creator), stored as the same JSON the
    // client sprite system reads: {"base","hair","outfit","accessory"}. Null == they
    // never opened the creator, so they get the deterministic name-hash default. Guests
    // persist by username, so the same guest name later resumes the same character.
    public string? CharacterJson { get; set; }

    // Last time this account was active (guest resume / login / OAuth callback / game
    // end). Drives guest data retention: guest accounts idle past Retention:GuestDays
    // get hard-deleted by the daily cleanup job. Null on rows created before phase 13.
    public DateTime? LastSeenUtc { get; set; }

    // A brand-new OAuth account that hasn't picked a display name yet. The client routes
    // these to /choose-username before anything else; the username POST clears the flag
    // (and can merge a prior guest identity). False for guest + password accounts.
    public bool NeedsUsername { get; set; }
}
