namespace GameApi.Models;

// An operator-granted reward held by a user (phase 18 admin dashboard). Cosmetic rewards
// ("outfit:<id>" / "accessory:<id>") are permanent unlocks — they gate what the player can
// SAVE in the character creator beyond the free set, and are never consumed. "cheat_card"
// is a one-time-per-game bonus: at game start the oldest unconsumed one is stamped
// ConsumedAt and the player seats with a 4th fake-out token that game.
public class UserReward
{
    public int Id { get; set; }
    public string UserId { get; set; } = default!;
    public ApplicationUser? User { get; set; }

    // "outfit:<id>", "accessory:<id>", or "cheat_card".
    public string Kind { get; set; } = default!;

    public DateTime GrantedAt { get; set; }

    // Only meaningful for consumable rewards (cheat_card). Null == still available.
    public DateTime? ConsumedAt { get; set; }
}
