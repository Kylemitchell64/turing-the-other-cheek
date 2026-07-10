// Deterministic character config from a display name. A player who hasn't opened the
// (phase 12) character creator still gets a stable, plausible character — and so does
// the AI, automatically, from its fake name. Same name in => same character out, so
// nothing about the derivation singles the AI out.

export const BASE_COUNT = 8;
export const HAIR_COUNT = 10;
export const OUTFIT_COUNT = 10;
export const ACCESSORY_COUNT = 6; // 0..5, plus null for "none"

// The free set everyone can save without a reward — mirrors CharacterDefaults on the server.
// Outfits 6..9 and accessories 3..5 are "premium": reward-locked in the creator, and the
// server's PUT /profile/character revalidates the same gate. Kept in sync by CharacterHashTests.
export const FREE_OUTFIT_COUNT = 6;
export const FREE_ACCESSORY_COUNT = 3;

// FNV-1a — small, fast, stable across browsers (no crypto needed here).
function hashName(name) {
  let h = 0x811c9dc5;
  const s = name || "player";
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return h >>> 0;
}

// Spread the four fields across different slices of the hash so they vary independently.
export function configFromName(name) {
  const h = hashName(name);
  const acc = Math.floor(h / 800) % 7; // 0..6; 6 == no accessory
  return {
    base: h % BASE_COUNT,
    hair: Math.floor(h / 8) % HAIR_COUNT,
    outfit: Math.floor(h / 80) % OUTFIT_COUNT,
    accessory: acc === 6 ? null : acc,
  };
}

// Clamp an arbitrary (possibly stored) config into valid ranges, filling gaps from the
// name-derived defaults. Ready for the phase-12 creator to hand in partial configs.
export function normalizeConfig(config, name) {
  const d = configFromName(name);
  if (!config) return d;
  const clamp = (v, n, fallback) =>
    Number.isInteger(v) && v >= 0 && v < n ? v : fallback;
  return {
    base: clamp(config.base, BASE_COUNT, d.base),
    hair: clamp(config.hair, HAIR_COUNT, d.hair),
    outfit: clamp(config.outfit, OUTFIT_COUNT, d.outfit),
    accessory:
      config.accessory === null
        ? null
        : clamp(config.accessory, ACCESSORY_COUNT, d.accessory),
  };
}
