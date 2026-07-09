// First-use character-creator gate. After login/guest-login we route a brand-new
// player to the creator ONCE — but only if they've never saved a character AND never
// skipped the creator before. Skips are remembered per-username in localStorage so a
// returning guest on the same device sails straight through.

import { api } from "../api/client";

const skipKey = (username) => `charSkipped:${username || "anon"}`;

export function hasSkippedCreator(username) {
  try {
    return localStorage.getItem(skipKey(username)) === "1";
  } catch {
    return false; // private mode / storage blocked — just don't remember the skip
  }
}

export function markCreatorSkipped(username) {
  try {
    localStorage.setItem(skipKey(username), "1");
  } catch { /* storage blocked — nothing to persist, they'll be offered it again */ }
}

// Decide whether to send this freshly-authed user to the creator first. Returns false
// on any API hiccup so a flaky network never traps someone on the login screen.
export async function needsCreator(token, username) {
  if (hasSkippedCreator(username)) return false;
  const { ok, data } = await api.getCharacter(token);
  if (!ok) return false; // couldn't ask — don't block entry
  return data == null;   // null == no saved character yet → offer the creator
}
