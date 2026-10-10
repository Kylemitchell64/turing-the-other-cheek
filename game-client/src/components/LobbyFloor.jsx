import { useEffect, useRef, useState } from "react";
import CharacterSprite from "../sprites/CharacterSprite";
import { useAuth } from "../auth/AuthContext";
import { useLobby } from "../game/LobbyContext";
import { mascotBus } from "./mascotBus";

// Phase 33: the lobby's shared floor. On the LOBBY page only, everyone else in the room
// wanders the bottom of your screen next to your own mascot (MenuWanderer, which keeps all
// of its phase-32 behaviour). Nobody streams coordinates: each character's walk is a pure
// function of (lobby code, display name, server time), so every phone draws the same
// character in roughly the same spot from nothing but a shared seed and the clock skew
// the lobby already measures. Your own character is the exception — it's interactive on
// your phone, so on other phones it's simulated like everyone else.
//
// Poke someone else's character (a tap on it, with nothing interactive underneath — the
// same document-level hit test as the mascot, the sprite itself takes no pointer events)
// and the server tells the whole room. That character turns, stomps toward the poker's
// character and mutters the poker's name — on every phone, yours included.
//
// ANONYMITY: the lobby is humans only (the AI has no seat until StartGame), and LobbyPage
// is the only page that renders this. Nothing here exists on GamePage. Display names only.
// prefers-reduced-motion: not rendered at all.
const FNV_OFFSET = 2166136261;
const hash = (str) => {
  let h = FNV_OFFSET;
  for (let i = 0; i < str.length; i++) { h ^= str.charCodeAt(i); h = Math.imul(h, 16777619) >>> 0; }
  return h >>> 0;
};
// deterministic 0..1 for (seed, k)
const unit = (seed, k) => hash(`${seed}:${k}`) / 4294967296;
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
const ease = (x) => x * x * (3 - 2 * x);

// Where a seeded character stands at server time t: a slow stroll between seeded waypoints,
// walking for the first 60% of each leg and loitering for the rest.
function seededSpot(seed, t) {
  const leg = 11_000 + (seed % 4000);
  const at = t + (seed % leg);
  const k = Math.floor(at / leg);
  const frac = (at - k * leg) / leg;
  const a = 4 + 82 * unit(seed, k);
  const b = 4 + 82 * unit(seed, k + 1);
  if (frac < 0.6) return { pos: a + (b - a) * ease(frac / 0.6), dir: b >= a ? 1 : -1, walking: true };
  return { pos: b, dir: b >= a ? 1 : -1, walking: false };
}

const STOMP_MS = 2600;

export default function LobbyFloor() {
  const { user } = useAuth();
  const { lobby, clockSkew, mascotCrashed, pokeMascot } = useLobby();
  const myName = user?.displayName || user?.unique_name;

  const reduced =
    typeof window !== "undefined" &&
    window.matchMedia &&
    window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const wide = typeof window !== "undefined" && window.matchMedia && window.matchMedia("(min-width: 900px)").matches;
  const scale = wide ? 1.45 : 1;

  const code = lobby?.code;
  const players = lobby?.players ?? [];
  const mates = players.filter((p) => p.displayName !== myName);
  const matesKey = mates.map((p) => p.displayName).join("|");

  // per-character live state on this phone: { pos, dir, walking, mood, line, glitch }
  const live = useRef(new Map());
  const overrides = useRef(new Map()); // name -> { until, toward } while stomping
  const [, setFrame] = useState(0);

  // Relay my own crash-outs to the room (the server makes everyone else flinch). While
  // this is mounted the mascot knows it's on the shared floor (floorActive) and reacts to
  // the room; anywhere else it ignores lobby events entirely.
  useEffect(() => {
    if (reduced) return;
    mascotBus.floorActive += 1;
    const off = mascotBus.on((ev) => { if (ev.type === "self-crash") mascotCrashed(); });
    return () => { mascotBus.floorActive -= 1; off(); };
  }, [reduced, mascotCrashed]);

  // Reactions from the hub: a poke makes the target stomp at the poker; someone else's
  // crash-out glitches their character for a beat (MenuWanderer handles my own flinch).
  useEffect(() => {
    if (reduced) return;
    const timers = [];
    const off = mascotBus.on((ev) => {
      if (ev.type === "poked" && ev.target !== myName && live.current.has(ev.target)) {
        overrides.current.set(ev.target, { until: Date.now() + STOMP_MS, toward: ev.poker });
        const st = live.current.get(ev.target);
        st.mood = "mad";
        st.line = `${ev.poker}!!`;
        timers.push(setTimeout(() => { st.mood = null; st.line = ""; }, STOMP_MS + 400));
      }
      if (ev.type === "remote-crash" && live.current.has(ev.name)) {
        const st = live.current.get(ev.name);
        st.glitch = true;
        timers.push(setTimeout(() => { st.glitch = false; }, 900));
      }
    });
    return () => { off(); timers.forEach(clearTimeout); };
  }, [reduced, myName]);

  // The walk: every tick each character eases toward its seeded spot (or, mid-stomp,
  // toward the poker), capped to a walking speed so a stomp and the stroll back look like
  // walking rather than teleporting.
  useEffect(() => {
    if (reduced || !code) return;
    const names = matesKey ? matesKey.split("|") : [];
    const seen = new Set(names);
    for (const n of [...live.current.keys()]) {
      if (!seen.has(n)) { live.current.delete(n); mascotBus.positions.delete(n); }
    }
    const tick = () => {
      const t = Date.now() - (clockSkew || 0);
      for (const n of names) {
        const seed = hash(`${code}|${n}`);
        const spot = seededSpot(seed, t);
        let st = live.current.get(n);
        if (!st) { st = { pos: spot.pos, dir: spot.dir, walking: spot.walking, mood: null, line: "", glitch: false }; live.current.set(n, st); }
        const o = overrides.current.get(n);
        let target = spot.pos;
        let speed = 0.6;
        if (o && o.until > Date.now()) {
          const pokerAt = mascotBus.positions.get(o.toward);
          if (pokerAt != null) target = clamp(pokerAt + (pokerAt > st.pos ? -6 : 6), -2, 90);
          speed = 0.95;
        } else if (o) {
          overrides.current.delete(n);
        }
        const gap = target - st.pos;
        if (Math.abs(gap) > 0.05) {
          st.pos += clamp(gap, -speed, speed);
          st.dir = gap > 0 ? 1 : -1;
          st.walking = Math.abs(gap) > 0.3;
        } else {
          st.walking = !o && spot.walking;
          if (!o) st.dir = spot.dir;
        }
        mascotBus.positions.set(n, st.pos);
      }
      setFrame((f) => (f + 1) % 1_000_000);
    };
    tick();
    const id = setInterval(tick, 60);
    return () => clearInterval(id);
  }, [reduced, code, matesKey, clockSkew]);

  // Tap-to-poke, wired at the document level like the mascot's input (phase 31): only a
  // tap inside a character's box with nothing interactive under it, and never one that
  // landed on my own mascot (it gets first dibs). Nothing here calls preventDefault.
  const layerRef = useRef(null);
  useEffect(() => {
    if (reduced) return;
    const INTERACTIVE = 'button, a, input, textarea, select, label, [role="button"], .modal-backdrop, .music-widget';
    let tap = null;
    const inside = (el, x, y) => {
      const r = el.getBoundingClientRect();
      return x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;
    };
    const down = (e) => {
      tap = null;
      if (!e.isPrimary || !layerRef.current) return;
      const own = document.querySelector(".home-robot-walker");
      if (own && inside(own, e.clientX, e.clientY)) return;
      const under = document.elementFromPoint(e.clientX, e.clientY);
      if (under && under.closest(INTERACTIVE)) return;
      const hit = [...layerRef.current.querySelectorAll(".floormate")].find((el) => inside(el, e.clientX, e.clientY));
      if (hit) tap = { id: e.pointerId, x: e.clientX, y: e.clientY, t: performance.now(), name: hit.dataset.name };
    };
    const up = (e) => {
      const s = tap;
      tap = null;
      if (!s || s.id !== e.pointerId || e.type === "pointercancel") return;
      if (Math.hypot(e.clientX - s.x, e.clientY - s.y) < 10 && performance.now() - s.t < 500) pokeMascot(s.name);
    };
    document.addEventListener("pointerdown", down);
    document.addEventListener("pointerup", up);
    document.addEventListener("pointercancel", up);
    return () => {
      document.removeEventListener("pointerdown", down);
      document.removeEventListener("pointerup", up);
      document.removeEventListener("pointercancel", up);
    };
  }, [reduced, pokeMascot]);

  if (reduced || !code || mates.length === 0) return null;

  return (
    <div className="lobby-floor" aria-hidden="true" ref={layerRef}>
      {mates.map((p) => {
        const st = live.current.get(p.displayName);
        if (!st) return null;
        const state = st.glitch ? "confused" : st.mood === "mad" ? "mad" : "neutral";
        return (
          <div
            key={p.displayName}
            className={`floormate${st.mood === "mad" ? " stomping" : ""}${st.glitch ? " glitching" : ""}${st.walking ? " walking" : ""}`}
            data-name={p.displayName}
            style={{ left: `${st.pos}%` }}
          >
            <div className="floormate-body" style={{ transform: `scaleX(${st.dir})` }}>
              <CharacterSprite name={p.displayName} config={p.character} state={state} size={50 * scale} />
            </div>
            <div className="floormate-name">{p.displayName}</div>
            {st.line && <div className="wanderer-mutter floormate-mutter">{st.line}</div>}
          </div>
        );
      })}
    </div>
  );
}
