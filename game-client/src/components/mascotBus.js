// Phase 33: a tiny in-page bus between the menu mascot (MenuWanderer), the lobby's shared
// floor (LobbyFloor) and the hub (LobbyContext). Same no-context-threading idea as sfxBus.
// Events:
//   { type: "self-crash" }               my mascot just crashed out (LobbyFloor relays it)
//   { type: "remote-crash", name }       someone else's did (from the hub)
//   { type: "poked", poker, target }     someone poked someone's character (from the hub)
// `positions` is where each character currently stands on THIS phone's floor (viewport %
// of its left edge), so a character can turn toward / stomp at another one. `floorActive`
// is > 0 only while LobbyFloor is mounted (the lobby page): the mascot ignores the room's
// events everywhere else.
const listeners = new Set();

export const mascotBus = {
  on(fn) { listeners.add(fn); return () => listeners.delete(fn); },
  emit(ev) { listeners.forEach((fn) => { try { fn(ev); } catch { /* cosmetic */ } }); },
  positions: new Map(),
  floorActive: 0,
};
