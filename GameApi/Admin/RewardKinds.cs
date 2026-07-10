using GameApi.Characters;

namespace GameApi.Admin;

// Shared parsing + validation for UserReward.Kind strings. Kinds are:
//   "outfit:<id>"     — unlock saving that outfit index in the creator (id in real range)
//   "accessory:<id>"  — unlock saving that accessory index in the creator
//   "cheat_card"      — one-time +1 fake-out token, consumed at game start
public static class RewardKinds
{
    public const string CheatCard = "cheat_card";

    // Validate a kind string an admin is trying to grant. Cosmetic ids must be inside the
    // sprite ranges AND above the free set (granting a free id would be a no-op unlock).
    public static bool IsGrantable(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return false;
        kind = kind.Trim();
        if (kind == CheatCard) return true;

        if (TryOutfit(kind, out var outfit))
            return outfit >= CharacterDefaults.FreeOutfitCount && outfit < CharacterDefaults.OutfitCount;
        if (TryAccessory(kind, out var acc))
            return acc >= CharacterDefaults.FreeAccessoryCount && acc < CharacterDefaults.AccessoryCount;
        return false;
    }

    public static bool TryOutfit(string kind, out int id) => TryPrefixed(kind, "outfit:", out id);
    public static bool TryAccessory(string kind, out int id) => TryPrefixed(kind, "accessory:", out id);

    private static bool TryPrefixed(string kind, string prefix, out int id)
    {
        id = -1;
        if (kind == null || !kind.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return int.TryParse(kind.AsSpan(prefix.Length), out id);
    }
}
