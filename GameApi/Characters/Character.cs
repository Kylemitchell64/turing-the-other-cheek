using System.Text.Json;

namespace GameApi.Characters;

// The saved character shape, mirroring the client sprite config
// (game-client/src/sprites/config.js). Serializes camelCase to
// {"base","hair","outfit","accessory"} — the exact JSON the client stores + renders.
// accessory is nullable: null == "no accessory".
public record CharacterConfig(int Base, int Hair, int Outfit, int? Accessory);

// Server-side twin of the client's sprite config: the SAME layer counts and the SAME
// deterministic name-hash default so every roster seat (humans without a saved
// character AND the AI, which can never save one) gets an identical character on both
// ends. The hash is ported byte-for-byte from sprites/config.js — CharacterHashTests
// pins the two implementations together with shared vectors.
public static class CharacterDefaults
{
    // Real ranges from sprites/config.js. Mirrored here so PUT validation can't drift.
    public const int BaseCount = 8;      // base   0..7
    public const int HairCount = 10;     // hair   0..9
    public const int OutfitCount = 10;   // outfit 0..9
    public const int AccessoryCount = 6; // accessory 0..5, or null for none

    // FNV-1a, identical to the JS hashName: 32-bit unsigned, wrapping multiply.
    public static uint HashName(string? name)
    {
        uint h = 0x811c9dc5;
        var s = string.IsNullOrEmpty(name) ? "player" : name;
        foreach (var c in s)
        {
            h ^= c;              // char -> uint (UTF-16 code unit, same as charCodeAt)
            h *= 0x01000193;     // uint multiply wraps mod 2^32, same as Math.imul here
        }
        return h;
    }

    // Deterministic default from a display name — the exact math from configFromName.
    public static CharacterConfig FromName(string? name)
    {
        var h = HashName(name);
        var acc = (h / 800) % 7; // 0..6; 6 == no accessory
        return new CharacterConfig(
            Base: (int)(h % BaseCount),
            Hair: (int)((h / 8) % HairCount),
            Outfit: (int)((h / 80) % OutfitCount),
            Accessory: acc == 6 ? null : (int)acc);
    }

    // Resolve the character to send for a roster seat: the player's saved config if it
    // parses + validates, otherwise the name-hash default. Also what the AI seat uses
    // (it never has a saved config, so it always resolves to its fake name's default).
    public static CharacterConfig Resolve(string? savedJson, string name)
    {
        if (!string.IsNullOrEmpty(savedJson) &&
            TryParse(savedJson, out var cfg, out _) && cfg != null)
            return cfg;
        return FromName(name);
    }

    // Validate + parse a character JSON payload. Enforces: an object with ONLY the four
    // known keys, every index an integer inside its real range (accessory may be null).
    // Any unknown key, missing key, wrong type, or out-of-range index is rejected.
    public static bool TryParse(string json, out CharacterConfig? config, out string? error)
    {
        config = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return TryParse(doc.RootElement, out config, out error);
        }
        catch (JsonException)
        {
            error = "character must be a JSON object";
            return false;
        }
    }

    public static bool TryParse(JsonElement el, out CharacterConfig? config, out string? error)
    {
        config = null;
        error = null;

        if (el.ValueKind != JsonValueKind.Object)
        {
            error = "character must be an object";
            return false;
        }

        // Known keys only — reject anything else so a client can't smuggle extra fields.
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Name is not ("base" or "hair" or "outfit" or "accessory"))
            {
                error = $"unknown field '{prop.Name}'";
                return false;
            }
        }

        if (!TryIndex(el, "base", BaseCount, out var baseVal, out error)) return false;
        if (!TryIndex(el, "hair", HairCount, out var hair, out error)) return false;
        if (!TryIndex(el, "outfit", OutfitCount, out var outfit, out error)) return false;

        // accessory: required key, but may be null ("no accessory") or a valid index.
        int? accessory;
        if (!el.TryGetProperty("accessory", out var accEl))
        {
            error = "missing field 'accessory'";
            return false;
        }
        if (accEl.ValueKind == JsonValueKind.Null)
        {
            accessory = null;
        }
        else if (!TryIndex(el, "accessory", AccessoryCount, out var acc, out error))
        {
            return false;
        }
        else
        {
            accessory = acc;
        }

        config = new CharacterConfig(baseVal, hair, outfit, accessory);
        return true;
    }

    private static bool TryIndex(JsonElement el, string name, int count, out int value, out string? error)
    {
        value = 0;
        error = null;
        if (!el.TryGetProperty(name, out var prop))
        {
            error = $"missing field '{name}'";
            return false;
        }
        if (prop.ValueKind != JsonValueKind.Number || !prop.TryGetInt32(out var v))
        {
            error = $"'{name}' must be an integer";
            return false;
        }
        if (v < 0 || v >= count)
        {
            error = $"'{name}' out of range";
            return false;
        }
        value = v;
        return true;
    }

    // Compact JSON for storage, in the client's key order.
    public static string ToJson(CharacterConfig cfg) =>
        JsonSerializer.Serialize(cfg, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
