import { useEffect, useRef, useState, useCallback } from "react";
import RobotSprite from "../sprites/RobotSprite";
import CharacterSprite from "../sprites/CharacterSprite";
import { useAuth } from "../auth/AuthContext";
import { api } from "../api/client";
import { sfxBus } from "../audio/sfx";

// The menu mascot (phase 25, grown out of the phase-21 HomeRobot). It wanders along the
// bottom of every menu screen, behind the panels: walks left-right, flips at the edges,
// stops now and then to look around. If you're signed in with a saved character it's YOU
// down there; otherwise (login screen, or nobody's saved a look yet) it's the robot.
//
// Phase 27: pinned to the bottom of the VIEWPORT (position:fixed) so it's always in frame
// wherever you've scrolled — and it reacts to the scroll with a little gravity. Scroll down
// and it "falls" to catch up, landing with a squash; scroll up and it floats back up, with a
// small settle. All of that is a spring driven imperatively off rAF (no 60fps re-renders),
// applied to an inner wrapper so it never fights the left/right walk or the facing flip.
//
// Phase 28: you can mess with it. A click/tap is a poke; press-and-drag picks it up and
// drops it somewhere else (it falls, lands with a squash). Every poke/drop is a "nudge" and
// nudges are counted in a sliding 30s window. 1-3 it's puzzled, 4-6 it's annoyed, 7-10 it's
// properly mad (red flash, stomps, backs away from your pointer). Nudge #11 in the window
// is one too many: it CRASHES OUT — glitches into glyphs, sprints off the nearest screen
// edge, falls in from the top of the screen (there's a portal, apparently), faceplants,
// lies there seeing stars, then gets up, dusts itself off and goes back to wandering with
// a clean slate. prefers-reduced-motion: a single static mascot — no wander, no physics,
// no interaction.
//
// Phase 32: more toys, and a way back down.
//   - THROW: let go mid-swing and it keeps the drag's velocity — flies sideways and up,
//     bounces off the viewport walls and ceiling, then falls and lands with the same
//     squash. A hard throw counts as two nudges (it noticed).
//   - PET: a slow sideways stroke across it (not a lift, not a flick) is petting: hearts, a
//     content eyes-shut look, and each stroke takes a nudge back OUT of the 30s window, so
//     its temper actually comes down. Pet it a lot while it's calm and it gets clingy for a
//     few seconds — trots after your finger. It only reads the pointer; it never captures it.
//   - SNACK: tap empty floor near the bottom (nothing interactive under the finger) and a
//     snack drops there; it walks over and eats it, which calms it too.
//   - ENDINGS: the crash-out picks one of four at random — the original portal faceplant,
//     a rage-quit ("CONNECTION LOST" sign, blinks out, sheepishly reconnects), packing a
//     tiny suitcase and leaving for ~20s, or a second robot walking in to scold YOU.
//   - SCARS: every crash-out leaves a mark for the rest of the browser session (bandage,
//     black eye, stitches, sling — capped). Module-level, so it survives page changes and
//     a reload clears it. Mascot-only: drawn as an overlay here, never inside
//     CharacterSprite, so podiums and every in-game sprite are untouched (ADR 0005).
// Every one of these is off under prefers-reduced-motion, and all of the new gestures go
// through the same phase-31 document-level hit test: the sprite still takes no pointer
// events and nothing here calls preventDefault on a tap meant for real UI.
const rand = (lo, hi) => lo + Math.random() * (hi - lo);
const randInt = (lo, hi) => Math.floor(rand(lo, hi + 1));
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
const now = () => Date.now();

const NUDGE_WINDOW_MS = 30_000;
const NUDGE_LIMIT = 10; // the 11th nudge inside the window triggers the crash-out

// what it mutters after each nudge, indexed by how many it's taken lately
const MUTTERS = ["", "?", "hey", "hm.", "stop", "quit it", "seriously.", "STOP", "grr", "!!!", "LAST WARNING"];
const GLYPHS = "▓▒░█▚▞╳@#%&*<>/\\|=+~";
const PET_MUTTERS = ["♥", "mm", "...ok", "♥♥", "purr", "fine. that's nice."];

// phase 32: throw tuning. Pointer speeds are px/ms over the last VELOCITY_MS of the drag.
const VELOCITY_MS = 150;
const STILL_MS = 140; // no move for this long before release = it was held still
const THROW_SPEED = 0.55; // slower than this on release = a plain drop (phase-28 behavior)
const HARD_THROW_SPEED = 1.6; // a hard throw costs two nudges
// pet: a slow, mostly-sideways stroke. Lift it (or move fast) and it's a pickup instead.
const PET_MAX_SPEED = 0.4;
const PET_STROKE_PX = 36; // this much rubbing = one stroke = one nudge forgiven
const CLINGY_PETS = 10; // strokes inside PET_WINDOW_MS while calm => clingy
const PET_WINDOW_MS = 8_000;
const CLINGY_MS = 5_000;
const SNACK_CALM = 3; // nudges a snack takes back

// phase 32: crash-out endings. window.__mascotTest = { ending, awayMs } lets the e2e force
// one (randomness is otherwise untestable); it's inert unless something sets it.
const ENDINGS = ["portal", "ragequit", "suitcase", "scold"];
const testHook = () => (typeof window !== "undefined" && window.__mascotTest) || {};
const pickEnding = () => {
  const forced = testHook().ending;
  return ENDINGS.includes(forced) ? forced : ENDINGS[randInt(0, ENDINGS.length - 1)];
};

// phase 32: battle scars. Session-only on purpose: a module variable survives SPA page
// changes (Home -> Stats keeps the bandage) and a reload wipes it.
const SCARS = ["bandage", "blackeye", "stitches", "sling"];
let sessionScars = 0;

// Speech bubbles are centered over the sprite, which clips the longer phase-32 lines off
// the side of a phone when it's near an edge. This is the screen-space px shift that keeps
// a bubble of roughly this text's width inside the viewport.
const bubbleShift = (pos, text, spriteW) => {
  const vw = (typeof window !== "undefined" && window.innerWidth) || 400;
  const half = String(text).length * 3.7 + 10;
  const center = (pos / 100) * vw + spriteW / 2;
  return Math.max(0, half + 4 - center) - Math.max(0, center + half + 4 - vw);
};

const moodFor = (n) => (n <= 0 ? "calm" : n <= 3 ? "annoyed" : n <= 6 ? "irritated" : "angry");

export default function MenuWanderer() {
  const { token, user } = useAuth();
  const username = user?.displayName || user?.unique_name || "player";

  const reduced =
    typeof window !== "undefined" &&
    window.matchMedia &&
    window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  // Bigger mascot on a desktop-sized viewport; phones keep the phase-25 size exactly.
  const [wide, setWide] = useState(
    () => typeof window !== "undefined" && window.matchMedia && window.matchMedia("(min-width: 900px)").matches,
  );
  useEffect(() => {
    if (!window.matchMedia) return;
    const mq = window.matchMedia("(min-width: 900px)");
    const on = (e) => setWide(e.matches);
    mq.addEventListener?.("change", on);
    return () => mq.removeEventListener?.("change", on);
  }, []);
  const scale = wide ? 1.45 : 1;

  // The saved character to wear. null = show the robot (no token, or nothing saved yet).
  const [character, setCharacter] = useState(null);
  useEffect(() => {
    if (!token) { setCharacter(null); return; }
    let alive = true;
    (async () => {
      const { ok, data } = await api.getCharacter(token);
      if (alive) setCharacter(ok && data ? data : null);
    })();
    return () => { alive = false; };
  }, [token]);

  // pos = left %, dir = facing (1 right / -1 left), walking, look = head glance (-1/0/1).
  const [pose, setPose] = useState({ pos: 18, dir: 1, walking: true, look: 0 });
  const phase = useRef({ ticks: randInt(60, 150) }); // ticks left in the current walk/pause
  // last committed pos, for scripted beats that need to know where it is without putting
  // side effects in a setPose updater (StrictMode runs those twice)
  const posRef = useRef(pose.pos);
  useEffect(() => { posRef.current = pose.pos; }, [pose.pos]);

  // ---- phase 28: temper + crash-out state ----
  // mode: idle | held | pet | flying | snack | eat | glitch | flee | fall | down | dust |
  // ragequit | offline | sheepish | pack | leave | away | return | sulk. Anything but idle
  // suspends the wander loop; scripted beats drive pos/offY themselves.
  const mode = useRef("idle");
  const nudges = useRef([]); // timestamps of recent pokes/drops
  const [mood, setMood] = useState("calm");
  const [mutter, setMutter] = useState("");
  const [fx, setFx] = useState(null); // "glitch" | "stars" | "dust" | "hearts" | "crumbs" | null
  const [fxKey, setFxKey] = useState(0); // remounts the particles so repeat hearts re-burst
  const [held, setHeld] = useState(false);
  const [dropping, setDropping] = useState(false); // released, still falling to the floor
  const [crashStage, setCrashStage] = useState(null); // mirrors mode for CSS classes
  const [act, setAct] = useState(null); // phase 32 calm-down beats: "pet" | "eat" | "clingy"
  const [snack, setSnack] = useState(null); // { x } — viewport %, where it landed
  const [scolder, setScolder] = useState(null); // { pos, dir, walking, line } the 2nd robot
  const [lastEnding, setLastEnding] = useState(null);
  const [scars, setScars] = useState(() => sessionScars);
  const timers = useRef([]);
  const after = useCallback((ms, fn) => { const t = setTimeout(fn, ms); timers.current.push(t); return t; }, []);
  useEffect(() => () => timers.current.forEach(clearTimeout), []);

  const burst = useCallback((kind) => { setFx(kind); setFxKey((k) => k + 1); }, []);

  // Scripted walks (flee, suitcase exit/return, walking to a snack). The wander tick moves
  // pos toward `to`; the effect below fires onArrive once it gets there.
  const walkTarget = useRef(null); // { to, dir, speed, onArrive }
  const cling = useRef({ until: 0, x: null }); // phase 32 clingy: where the finger last was
  const pets = useRef([]); // stroke timestamps, for clinginess

  // the walker's width as a % of the viewport, for edge math (thrown walls, snack aim)
  const walkerRef = useRef(null);
  const widthPct = useCallback(() => {
    const r = walkerRef.current?.getBoundingClientRect();
    return r ? (r.width / (window.innerWidth || 1)) * 100 : 6;
  }, []);

  useEffect(() => {
    if (reduced) return;
    const id = setInterval(() => {
      // temper cools as nudges age out of the window
      const t = now();
      const before = nudges.current.length;
      nudges.current = nudges.current.filter((n) => t - n < NUDGE_WINDOW_MS);
      if (nudges.current.length !== before) setMood(moodFor(nudges.current.length));

      setPose((p) => {
        const w = walkTarget.current;
        if (w) {
          const pos = p.pos + w.speed * w.dir;
          const done = w.dir > 0 ? pos >= w.to : pos <= w.to;
          return { pos: done ? w.to : pos, dir: w.dir, walking: true, look: 0 };
        }
        if (mode.current !== "idle") return p;

        // phase 32: clingy — trot after the finger instead of wandering
        const c = cling.current;
        if (c.until > t && c.x != null) {
          const target = clamp((c.x / (window.innerWidth || 1)) * 100 - 3, 3, 86);
          const gap = target - p.pos;
          if (Math.abs(gap) < 1.5) return { ...p, walking: false, look: 0 };
          const dir = gap > 0 ? 1 : -1;
          return { pos: p.pos + Math.min(Math.abs(gap), 0.75) * dir, dir, walking: true, look: 0 };
        }

        const ph = phase.current;
        ph.ticks -= 1;
        const angry = nudges.current.length >= 7;
        if (p.walking) {
          let pos = p.pos + (angry ? 0.55 : 0.32) * p.dir; // stomps around faster when mad
          let dir = p.dir;
          if (pos <= 3) { pos = 3; dir = 1; }
          if (pos >= 86) { pos = 86; dir = -1; }
          if (ph.ticks <= 0) {
            phase.current = { ticks: randInt(28, 64) }; // pause for a beat
            return { pos, dir, walking: false, look: [-1, 0, 1][randInt(0, 2)] };
          }
          return { ...p, pos, dir };
        }
        // paused: occasionally glance a different way, then resume walking
        let look = p.look;
        if (Math.random() < 0.04) look = [-1, 0, 1][randInt(0, 2)];
        if (ph.ticks <= 0) {
          phase.current = { ticks: randInt(70, 170) };
          return { ...p, walking: true, look: 0 };
        }
        return { ...p, look };
      });

      // clinginess wears off on its own
      if (cling.current.until && cling.current.until <= t) {
        cling.current = { until: 0, x: null };
        setAct((a) => (a === "clingy" ? null : a));
      }
    }, 60);
    return () => clearInterval(id);
  }, [reduced]);

  // a scripted walk reached its target
  useEffect(() => {
    const w = walkTarget.current;
    if (!w || pose.pos !== w.to) return;
    walkTarget.current = null;
    w.onArrive?.();
  }, [pose.pos]);

  const walkTo = useCallback((to, speed, onArrive) => {
    const from = posRef.current;
    if (Math.abs(from - to) < 0.01) { after(30, () => onArrive?.()); return; }
    const dir = to > from ? 1 : -1;
    walkTarget.current = { to, dir, speed, onArrive };
    setPose((p) => ({ ...p, dir, walking: true, look: 0 }));
  }, [after]);

  // Blink on a slow random cadence (only for the character mascot — the robot LED blinks in
  // CSS). Same dressing-room idle beat as the creator preview.
  const [blink, setBlink] = useState(false);
  useEffect(() => {
    if (reduced || !character) return;
    const timers = [];
    const loop = () => {
      setBlink(true);
      timers.push(setTimeout(() => setBlink(false), 140));
      timers.push(setTimeout(loop, rand(2600, 5600)));
    };
    timers.push(setTimeout(loop, rand(1500, 3000)));
    return () => timers.forEach(clearTimeout);
  }, [reduced, character]);

  // ---- vertical physics (phase 27 spring, phase 28 gravity, phase 32 throw) ----
  // The mascot rests pinned to the viewport bottom (offY 0). A scroll kicks offY away from
  // home and a spring pulls it back. Being dropped or falling from the portal switches to
  // plain gravity with a floor at 0 and a bounce. Vertical speed stretches it thin mid-air;
  // landing squashes it. All applied straight to the DOM node — no re-render per frame.
  // Phase 32: a throw adds a horizontal axis to the same gravity loop (posX/velX in viewport
  // %), with walls at the viewport edges and a ceiling at the top. posX is written straight
  // to the walker's style.left like the drag does; React's pose.pos catches up on landing.
  const bodyRef = useRef(null);
  const phys = useRef({
    offY: 0, velY: 0, squash: 0, rot: 0, spin: 0, gravity: false, raf: 0, lastY: 0, onLand: null,
    flying: false, posX: 0, velX: 0, maxX: 90, ceil: 600,
  });

  const apply = useCallback(() => {
    const el = bodyRef.current;
    const p = phys.current;
    if (!el) return;
    const stretch = clamp(Math.abs(p.velY) * 0.014, 0, 0.32); // thin + tall while airborne
    const sy = clamp(1 + stretch - p.squash, 0.62, 1.4);
    const sx = clamp(1 - stretch * 0.6 + p.squash * 0.85, 0.7, 1.4);
    el.style.transform =
      `translateY(${p.offY.toFixed(2)}px) rotate(${p.rot.toFixed(1)}deg) scale(${sx.toFixed(3)}, ${sy.toFixed(3)})`;
    if (p.flying && walkerRef.current) walkerRef.current.style.left = `${p.posX.toFixed(2)}%`;
  }, []);

  const kick = useCallback(() => {
    const p = phys.current;
    if (p.raf) return;
    const frame = () => {
      const before = p.offY;
      if (p.gravity) {
        p.velY += 0.85; // fall
        p.offY += p.velY;
        p.rot += p.spin;
        if (p.flying) {
          // phase 32: sideways flight, bouncing off the walls and the ceiling
          p.posX += p.velX;
          p.velX *= 0.992; // a little air drag
          if (p.posX < 0 || p.posX > p.maxX) {
            p.posX = clamp(p.posX, 0, p.maxX);
            p.velX = -p.velX * 0.62;
            p.spin = -p.spin * 0.7;
            p.squash = clamp(p.squash + 0.18, 0, 0.42);
            sfxBus.emit("land");
          }
          if (p.offY < -p.ceil) {
            p.offY = -p.ceil;
            p.velY = Math.abs(p.velY) * 0.45;
          }
        }
        if (p.offY >= 0) {
          // hit the floor
          const impact = p.velY;
          p.offY = 0;
          p.squash = clamp(p.squash + impact * 0.035, 0, 0.42);
          p.velX *= 0.5; // floor friction eats most of the sideways speed
          if (impact > 9) {
            p.velY = -impact * 0.28; // little bounce
          } else {
            p.velY = 0; p.gravity = false; p.spin = 0; p.rot = 0; p.velX = 0;
            const cb = p.onLand; p.onLand = null;
            if (cb) cb(impact);
          }
        }
      } else {
        p.velY += -0.16 * p.offY; // spring toward home
        p.velY *= 0.74; // damping
        p.offY += p.velY;
        // crossed the resting spot with some speed => it just landed/settled: squash a bit,
        // capped so a fast flick can't flatten it into a pancake.
        if (before !== 0 && Math.sign(before) !== Math.sign(p.offY) && Math.abs(p.velY) > 0.4) {
          p.squash = clamp(p.squash + Math.abs(p.velY) * 0.03, 0, 0.3);
        }
      }
      p.squash *= 0.82;
      apply();
      if (p.gravity || Math.abs(p.offY) > 0.15 || Math.abs(p.velY) > 0.15 || p.squash > 0.01) {
        p.raf = requestAnimationFrame(frame);
      } else {
        p.offY = 0; p.velY = 0; p.squash = 0;
        apply();
        p.raf = 0;
      }
    };
    p.raf = requestAnimationFrame(frame);
  }, [apply]);

  useEffect(() => {
    if (reduced) return;
    const p = phys.current;
    p.lastY = typeof window !== "undefined" ? window.scrollY || 0 : 0;

    const onScroll = () => {
      if (mode.current !== "idle") return;
      const y = window.scrollY || 0;
      const dy = y - p.lastY;
      p.lastY = y;
      if (!dy) return;
      // down (dy>0) => displace UP so it falls back down; up => displace down so it rises.
      p.offY = clamp(p.offY - clamp(dy, -80, 80) * 0.55, -42, 42);
      kick();
    };

    window.addEventListener("scroll", onScroll, { passive: true });
    return () => {
      window.removeEventListener("scroll", onScroll);
      if (p.raf) cancelAnimationFrame(p.raf);
      p.raf = 0;
    };
  }, [reduced, kick]);

  // ---- phase 28: nudges ----
  const say = useCallback((text, ms = 1300) => {
    setMutter(text);
    after(ms, () => setMutter((m) => (m === text ? "" : m)));
  }, [after]);

  // Every ending finishes here: clean slate, plus one more scar for the session.
  const recover = useCallback(() => {
    nudges.current = [];
    setMood("calm");
    sessionScars = Math.min(SCARS.length, sessionScars + 1);
    setScars(sessionScars);
    setFx(null);
    setCrashStage(null);
    phase.current = { ticks: randInt(30, 60) };
    mode.current = "idle";
    setPose((q) => ({ ...q, walking: false, look: 0 }));
  }, []);

  const stage = useCallback((m) => { mode.current = m; setCrashStage(m); }, []);
  const edgeFor = (pos) => (pos < 45 ? -16 : 106); // just past whichever edge is closer

  // ending (a): the original — sprint off, fall back in through the "portal", faceplant
  const endPortal = useCallback(() => {
    const to = edgeFor(posRef.current);
    stage("flee");
    walkTo(to, 2.2, () => {
      stage("fall");
      const ph = phys.current;
      after(420, () => {
        setPose({ pos: randInt(12, 78), dir: 1, walking: false, look: 0 });
        ph.offY = -(window.innerHeight || 800) - 40;
        ph.velY = 0;
        ph.rot = 0;
        ph.spin = rand(3, 6) * (Math.random() < 0.5 ? -1 : 1);
        ph.gravity = true;
        ph.onLand = () => {
          stage("down");
          setFx("stars");
          sfxBus.emit("thud");
          after(2700, () => {
            // up (slowly), dust off, clean slate
            stage("dust");
            setFx("dust");
            sfxBus.emit("dust");
            nudges.current = [];
            setMood("calm");
            after(1700, recover);
          });
        };
        kick();
      });
    });
  }, [after, kick, recover, stage, walkTo]);

  // ending (b): rage-quit — "CONNECTION LOST" sign, blinks out, sheepishly reconnects
  const endRageQuit = useCallback(() => {
    stage("ragequit");
    sfxBus.emit("crash");
    after(2200, () => {
      stage("offline");
      after(1600, () => {
        stage("sheepish");
        say("...reconnected", 1800);
        after(1700, recover);
      });
    });
  }, [after, recover, say, stage]);

  // ending (c): packs a tiny suitcase, walks off, comes back ~20s later
  const endSuitcase = useCallback(() => {
    stage("pack");
    say("that's it. I'm leaving.", 1700);
    after(1700, () => {
      const to = edgeFor(posRef.current);
      stage("leave");
      walkTo(to, 0.7, () => {
        stage("away");
        after(testHook().awayMs ?? 20_000, () => {
          stage("return");
          walkTo(to < 0 ? randInt(10, 35) : randInt(50, 75), 0.6, () => {
            stage("sheepish");
            say("...forgot my charger.", 1800);
            after(1500, recover);
          });
        });
      });
    });
  }, [after, recover, say, stage, walkTo]);

  // ending (d): a second robot walks in and scolds the PLAYER, then leaves
  const endScold = useCallback(() => {
    stage("sulk");
    const pos = posRef.current;
    const fromRight = pos < 50;
    const edge = fromRight ? 106 : -16;
    const beside = clamp(pos + (fromRight ? 10 : -10), -4, 92);
    setPose((p) => ({ ...p, walking: false, look: fromRight ? 1 : -1, dir: fromRight ? 1 : -1 }));
    setScolder({ pos: edge, dir: fromRight ? -1 : 1, walking: true, line: "" });
    // next frame: slide to the mascot (CSS transition on left does the walk)
    after(40, () => setScolder((s) => s && { ...s, pos: beside }));
    after(1900, () => {
      const who = user ? username : "you";
      setScolder((s) => s && { ...s, walking: false, line: `hey. ${who}. leave it alone.` });
      sfxBus.emit("poke");
    });
    after(4300, () => setScolder((s) => s && { ...s, line: "pick on someone your own size." }));
    after(6300, () => setScolder((s) => s && { ...s, pos: edge, dir: -s.dir, walking: true, line: "" }));
    after(8200, () => { setScolder(null); recover(); });
  }, [after, recover, stage, user, username]);

  const crashOut = useCallback(() => {
    // 1) glitch out (every ending starts here), then 2) one of four ways to cope
    stage("glitch");
    setHeld(false);
    setAct(null);
    cling.current = { until: 0, x: null };
    setFx("glitch");
    setMutter("");
    sfxBus.emit("crash");
    const ending = pickEnding();
    setLastEnding(ending);
    after(950, () => {
      setFx(null);
      if (ending === "ragequit") endRageQuit();
      else if (ending === "suitcase") endSuitcase();
      else if (ending === "scold") endScold();
      else endPortal();
    });
  }, [after, stage, endPortal, endRageQuit, endSuitcase, endScold]);

  const nudge = useCallback((pointerX, count = 1) => {
    if (mode.current !== "idle") return;
    const t = now();
    nudges.current = nudges.current.filter((n) => t - n < NUDGE_WINDOW_MS);
    for (let i = 0; i < count; i++) nudges.current.push(t);
    cling.current = { until: 0, x: null };
    setAct((a) => (a === "clingy" ? null : a));
    const n = nudges.current.length;
    if (n > NUDGE_LIMIT) { crashOut(); return; }
    setMood(moodFor(n));
    say(count > 1 && n < 9 ? "WHOA" : MUTTERS[n] || "!!!");
    sfxBus.emit({ name: "poke", variant: n });
    // a poke bumps it a little; when it's mad it also backs away from your pointer
    const p = phys.current;
    p.offY = Math.min(p.offY, -(6 + n * 1.2));
    kick();
    setPose((q) => {
      const el = bodyRef.current;
      const rect = el?.getBoundingClientRect();
      const cx = rect ? rect.left + rect.width / 2 : null;
      const away = cx != null && pointerX != null ? (pointerX < cx ? 1 : -1) : q.dir;
      const look = cx != null && pointerX != null ? (pointerX < cx ? -1 : 1) : 0;
      if (n >= 7) {
        phase.current = { ticks: randInt(14, 26) };
        return { pos: clamp(q.pos + away * 3, 3, 86), dir: away, walking: true, look: 0 };
      }
      phase.current = { ticks: randInt(18, 40) };
      return { ...q, walking: false, look };
    });
  }, [crashOut, say, kick]);

  // ---- phase 32: calming it down ----
  // One stroke forgives one nudge. Lots of strokes while it's already calm => clingy.
  const petOnce = useCallback(() => {
    const t = now();
    nudges.current = nudges.current.filter((n) => t - n < NUDGE_WINDOW_MS);
    nudges.current.shift();
    const n = nudges.current.length;
    setMood(moodFor(n));
    burst("hearts");
    sfxBus.emit("pet");
    pets.current = pets.current.filter((s) => t - s < PET_WINDOW_MS);
    pets.current.push(t);
    if (n === 0 && pets.current.length >= CLINGY_PETS && cling.current.until <= t) {
      cling.current = { until: t + CLINGY_MS, x: null };
      pets.current = [];
      say("♥ more?", 1600);
    } else {
      say(PET_MUTTERS[Math.min(pets.current.length - 1, PET_MUTTERS.length - 1)] || "♥", 900);
    }
  }, [burst, say]);

  const calmBy = useCallback((k) => {
    nudges.current = nudges.current.slice(k);
    setMood(moodFor(nudges.current.length));
  }, []);

  const dropSnack = useCallback((clientX) => {
    const vw = window.innerWidth || 1;
    const x = clamp((clientX / vw) * 100, 2, 98);
    setSnack({ x });
    mode.current = "snack";
    say("!", 700);
    const to = clamp(x - widthPct() / 2, -4, 92);
    walkTo(to, 0.6, () => {
      mode.current = "eat";
      setAct("eat");
      setSnack(null);
      burst("crumbs");
      say("nom nom", 1100);
      sfxBus.emit("nom");
      after(1200, () => {
        calmBy(SNACK_CALM);
        setFx(null);
        setAct(null);
        phase.current = { ticks: randInt(30, 60) };
        mode.current = "idle";
        setPose((q) => ({ ...q, walking: false, look: 0 }));
      });
    });
  }, [after, burst, calmBy, say, walkTo, widthPct]);

  // ---- pointer: click = poke, drag = pick up & drop (or throw), slow rub = pet ----
  const drag = useRef(null);

  const onPointerDown = (e) => {
    if (reduced || mode.current !== "idle") return;
    e.preventDefault();
    const t = performance.now();
    drag.current = {
      id: e.pointerId, x0: e.clientX, y0: e.clientY, t0: t, moved: false, pet: false,
      startPos: pose.pos, samples: [{ x: e.clientX, y: e.clientY, t }], lastX: e.clientX, rub: 0,
    };
  };

  const pickUp = (d) => {
    d.moved = true;
    d.pet = false;
    mode.current = "held";
    setAct(null);
    setHeld(true);
    sfxBus.emit("grab");
    // Stop any in-flight spring/gravity frame loop: while held, the pointer owns the
    // position outright. (Leaving it running made the spring pull the sprite back
    // toward the floor between pointer events — the "drifts away from your finger" lag.)
    const p0 = phys.current;
    if (p0.raf) { cancelAnimationFrame(p0.raf); p0.raf = 0; }
    p0.gravity = false;
    p0.flying = false;
    p0.velY = 0;
    p0.squash = 0;
    setPose((q) => ({ ...q, walking: false, look: 0 }));
  };

  const onPointerMove = (e) => {
    const d = drag.current;
    if (!d || d.id !== e.pointerId) return;
    const t = performance.now();
    d.samples.push({ x: e.clientX, y: e.clientY, t });
    while (d.samples.length > 2 && t - d.samples[0].t > VELOCITY_MS) d.samples.shift();
    const dx = e.clientX - d.x0;
    const dy = e.clientY - d.y0;
    if (!d.moved && !d.pet) {
      const dist = Math.hypot(dx, dy);
      if (dist < 6) return;
      // phase 32: slow and sideways (not lifting) = a stroke; anything else = a pickup
      const speed = dist / Math.max(16, t - d.t0);
      if (Math.abs(dy) < Math.abs(dx) * 0.6 && dy > -8 && speed < PET_MAX_SPEED) {
        d.pet = true;
        mode.current = "pet";
        setAct("pet");
        d.lastX = e.clientX;
        return;
      }
      pickUp(d);
    }
    if (d.pet) {
      // lifting mid-stroke turns it into a pickup after all
      if (dy < -28) { pickUp(d); } else {
        d.rub += Math.abs(e.clientX - d.lastX);
        d.lastX = e.clientX;
        if (d.rub >= PET_STROKE_PX) { d.rub = 0; petOnce(); }
        return;
      }
    }
    const vw = window.innerWidth || 1;
    const pos = clamp(d.startPos + (dx / vw) * 100, -4, 92);
    // Write straight to the DOM (no React render, no CSS transition) so it tracks the
    // finger frame-for-frame; the state catches up on release.
    d.pos = pos;
    if (walkerRef.current) walkerRef.current.style.left = `${pos}%`;
    const p = phys.current;
    p.offY = Math.min(0, dy - 4); // lifted straight up under the pointer
    p.rot = -clamp(dx * 0.08, -14, 14);
    apply();
  };

  // release velocity in px/ms across the last VELOCITY_MS of pointer MOVES. The release
  // point itself is left out — pointerup usually lands exactly where the last move did, so
  // counting it would read a flick as slower than it was. A finger that sat still for
  // STILL_MS before letting go has no velocity, so a careful drop stays a drop.
  const releaseVelocity = (d) => {
    const s = d.samples;
    const b = s[s.length - 1];
    if (!b || performance.now() - b.t > STILL_MS) return { vx: 0, vy: 0 };
    let a = b;
    for (let i = s.length - 2; i >= 0; i--) {
      a = s[i];
      if (b.t - a.t > VELOCITY_MS) break;
    }
    if (a === b) return { vx: 0, vy: 0 };
    const dt = Math.max(8, b.t - a.t);
    return { vx: (b.x - a.x) / dt, vy: (b.y - a.y) / dt };
  };

  const onPointerUp = (e) => {
    const d = drag.current;
    if (!d || d.id !== e.pointerId) return;
    drag.current = null;
    if (d.pet) {
      mode.current = "idle";
      setAct(cling.current.until > now() ? "clingy" : null);
      return;
    }
    if (!d.moved) { nudge(e.clientX); return; }
    // dropped: fall to the floor, land with a squash, then count it as a nudge. It stays
    // "dropping" (layer lifted above the panels) until it actually touches down.
    const pos = typeof d.pos === "number" ? d.pos : d.startPos;
    const { vx, vy } = e.type === "pointercancel" ? { vx: 0, vy: 0 } : releaseVelocity(d);
    const speed = Math.hypot(vx, vy);
    const thrown = speed >= THROW_SPEED;
    const hard = speed >= HARD_THROW_SPEED;
    setHeld(false);
    setDropping(true);
    const p = phys.current;
    p.spin = 0;
    p.rot = 0;
    p.gravity = true;
    p.velY = 0;
    if (thrown) {
      // phase 32: keep the swing. px/ms -> px/frame (vertical) and %/frame (horizontal).
      const vw = window.innerWidth || 1;
      mode.current = "flying";
      p.flying = true;
      p.posX = pos;
      p.maxX = Math.max(0, 100 - widthPct());
      p.ceil = Math.max(60, (window.innerHeight || 800) - 90);
      p.velX = clamp(((vx * 16.7) / vw) * 100, -5, 5);
      p.velY = clamp(vy * 16.7, -34, 12);
      if (p.offY >= -1) p.velY = Math.min(p.velY, -6); // a floor-level flick still hops
      p.spin = clamp(p.velX * 4, -14, 14);
      sfxBus.emit("grab");
    } else {
      setPose((q) => ({ ...q, pos }));
    }
    p.onLand = () => {
      if (p.flying) {
        p.flying = false;
        const landed = clamp(p.posX, -4, 92);
        setPose((q) => ({ ...q, pos: landed, dir: p.velX < 0 ? -1 : q.dir }));
      }
      mode.current = "idle";
      setDropping(false);
      sfxBus.emit("land");
      nudge(null, hard ? 2 : 1);
    };
    kick();
    // if it was dropped basically at floor level, land right away
    if (!thrown && p.offY >= -1) { p.gravity = false; p.offY = 0; const cb = p.onLand; p.onLand = null; cb?.(0); }
  };

  const style = {
    left: `${pose.pos}%`,
    transform: `scaleX(${pose.dir})`,
  };

  // sprite emotion for the character mascot; the robot gets the same via CSS classes
  const flying = dropping && phys.current.flying;
  let spriteState = "neutral";
  if (crashStage === "glitch") spriteState = "confused";
  else if (crashStage === "flee" || crashStage === "ragequit") spriteState = "mad";
  else if (crashStage === "fall" || crashStage === "return") spriteState = "confused";
  else if (crashStage === "down") spriteState = "defeated";
  else if (crashStage === "dust") spriteState = "excited";
  else if (crashStage === "sulk") spriteState = "defeated";
  else if (crashStage === "pack" || crashStage === "leave" || crashStage === "sheepish") spriteState = "sad";
  else if (held) spriteState = "confused";
  else if (act === "eat" || act === "clingy") spriteState = "excited";
  else if (mood === "angry") spriteState = "mad";
  else if (mood === "irritated") spriteState = "sad";
  else if (mood === "annoyed") spriteState = "confused";

  const bodyClass = [
    "wanderer-body",
    `mood-${mood}`,
    held ? "held" : "",
    crashStage ? `crash-${crashStage}` : "",
    act ? `act-${act}` : "",
    flying ? "thrown" : "",
  ].filter(Boolean).join(" ");

  const interactive = !reduced;

  // Input is wired at the DOCUMENT level, not on the sprite (phase 31). The sprite itself
  // takes no pointer events, so it can never sit between a finger and a button: a tap is
  // handed to it only when it lands inside the sprite's box AND nothing interactive is
  // underneath. While a drag is live, touch scrolling is suppressed so the page doesn't
  // pan under the finger.
  // Phase 32 adds two read-only listeners on the same path: a tap on bare floor (inside the
  // mascot's band, nothing interactive or panel under it) drops a snack, and while it's
  // clingy the last pointer x is remembered. Neither calls preventDefault — the tap still
  // goes wherever it was going.
  const layerRef = useRef(null);
  const snackTap = useRef(null);
  const handlersRef = useRef(null);
  handlersRef.current = { onPointerDown, onPointerMove, onPointerUp, dropSnack };
  useEffect(() => {
    if (!interactive) return;
    const INTERACTIVE = 'button, a, input, textarea, select, label, [role="button"], .modal-backdrop, .music-widget';
    const down = (e) => {
      if (cling.current.until > now()) cling.current.x = e.clientX;
      if (drag.current || !e.isPrimary) return;
      const w = walkerRef.current;
      if (!w) return;
      const r = w.getBoundingClientRect();
      const under = document.elementFromPoint(e.clientX, e.clientY);
      if (under && under.closest(INTERACTIVE)) return;
      if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) {
        // not on the sprite: maybe a tap on the empty floor (phase 32 snack)
        const band = layerRef.current?.getBoundingClientRect();
        if (band && e.clientY >= band.top && mode.current === "idle" && !(under && under.closest(".panel"))) {
          snackTap.current = { id: e.pointerId, x: e.clientX, y: e.clientY, t: performance.now() };
        }
        return;
      }
      handlersRef.current.onPointerDown(e);
    };
    const move = (e) => {
      if (drag.current) handlersRef.current.onPointerMove(e);
      else if (cling.current.until > now()) cling.current.x = e.clientX;
    };
    const up = (e) => {
      if (drag.current) { handlersRef.current.onPointerUp(e); return; }
      const s = snackTap.current;
      snackTap.current = null;
      if (!s || s.id !== e.pointerId || e.type === "pointercancel") return;
      const still = Math.hypot(e.clientX - s.x, e.clientY - s.y) < 10 && performance.now() - s.t < 500;
      if (still && mode.current === "idle") handlersRef.current.dropSnack(e.clientX);
    };
    const noScroll = (e) => { if (drag.current) e.preventDefault(); };
    document.addEventListener("pointerdown", down);
    document.addEventListener("pointermove", move);
    document.addEventListener("pointerup", up);
    document.addEventListener("pointercancel", up);
    document.addEventListener("touchmove", noScroll, { passive: false });
    return () => {
      document.removeEventListener("pointerdown", down);
      document.removeEventListener("pointermove", move);
      document.removeEventListener("pointerup", up);
      document.removeEventListener("pointercancel", up);
      document.removeEventListener("touchmove", noScroll);
    };
  }, [interactive]);

  const busy = held || dropping || crashStage || scolder;
  return (
    <div
      className={`home-robot${crashStage || scolder ? " crashing" : ""}${busy ? " active" : ""}`}
      aria-hidden="true"
      ref={layerRef}
      data-last-ending={lastEnding || undefined}
    >
      {snack && !reduced && <Snack x={snack.x} />}
      <div
        className={`home-robot-walker${interactive ? " grabbable" : ""}${held || flying ? " grabbing" : ""}`}
        style={style}
        ref={walkerRef}
      >
        <div className={bodyClass} ref={bodyRef} data-scars={reduced ? 0 : scars}>
          {character ? (
            <CharacterSprite
              name={username}
              config={character}
              state={spriteState}
              size={50 * scale}
              look={reduced ? null : { dx: pose.look * 1.1, dy: pose.look !== 0 ? 0.35 : 0 }}
              blink={blink || act === "pet"}
            />
          ) : (
            <RobotSprite size={60 * scale} walking={!reduced && pose.walking} looking={pose.look} />
          )}
          {!reduced && scars > 0 && <Scars count={scars} robot={!character} />}
          {fx && <Particles key={fxKey} kind={fx} />}
        </div>
        {(crashStage === "pack" || crashStage === "leave" || crashStage === "away" || crashStage === "return") && (
          <Suitcase />
        )}
        {crashStage === "ragequit" && <div className="wanderer-sign">CONNECTION LOST</div>}
        {/* mutter bubble is a child of the walker so it flips back to readable with dir */}
        {mutter && (
          <div
            className="wanderer-mutter"
            style={{ transform: `translateX(calc(-50% + ${bubbleShift(pose.pos, mutter, 40 * scale) * pose.dir}px)) scaleX(${pose.dir})` }}
          >
            {mutter}
          </div>
        )}
      </div>
      {scolder && (
        <div
          className="wanderer-scolder"
          style={{ left: `${scolder.pos}%`, transform: `scaleX(${scolder.dir})` }}
        >
          <div className="scolder-body">
            <RobotSprite size={60 * scale} walking={scolder.walking} looking={0} />
          </div>
          {scolder.line && (
            <div
              className="wanderer-mutter scold"
              style={{ transform: `translateX(calc(-50% + ${bubbleShift(scolder.pos, scolder.line, 42 * scale) * scolder.dir}px)) scaleX(${scolder.dir})` }}
            >
              {scolder.line}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// Phase 32 battle scars, drawn over the mascot in the sprite's own viewBox so they sit on
// the head/arm and ride the same transforms (the crash CSS targets `> svg`, which matches
// this overlay too). Lives here, NOT in CharacterSprite, so no podium can ever show one.
function Scars({ count, robot }) {
  const vb = robot ? "0 0 28 40" : "0 0 32 40";
  // robot head is x 5..23 y 4..16; the chibi head is x 6.5..25.5 y 2..19 (eyes at y ~11)
  const g = robot
    ? { band: [6, 4.2], eye: [16.2, 9.4], stitch: [8, 13.6], arm: [21.3, 22] }
    : { band: [9, 3.6], eye: [19.4, 11.8], stitch: [11, 15.2], arm: [23, 24] };
  const on = SCARS.slice(0, count);
  return (
    <svg className="wanderer-scars" viewBox={vb} shapeRendering="crispEdges" aria-hidden="true">
      {on.includes("bandage") && (
        <g className="wanderer-scar scar-bandage" transform={`translate(${g.band[0]} ${g.band[1]}) rotate(-18)`}>
          <rect x="0" y="0" width="8" height="2.6" rx="0.6" fill="#f4e1c1" stroke="#b89a6e" strokeWidth="0.3" />
          <rect x="3.2" y="0.5" width="1.6" height="1.6" fill="#e9c9a0" />
        </g>
      )}
      {on.includes("blackeye") && robot && (
        // the robot has no eye socket to bruise, so its "black eye" is a cracked visor
        <path className="wanderer-scar scar-blackeye" d="M17.5 7 L16 8.6 L17.2 9.6 L15.6 11.6 M16 8.6 L14.6 8"
          fill="none" stroke="#e8f4ff" strokeWidth="0.45" shapeRendering="geometricPrecision" />
      )}
      {on.includes("blackeye") && !robot && (
        <circle className="wanderer-scar scar-blackeye" cx={g.eye[0]} cy={g.eye[1]} r="2.1"
          fill="none" stroke="#4a2a5e" strokeWidth="0.9" opacity="0.85" shapeRendering="geometricPrecision" />
      )}
      {on.includes("stitches") && (
        <g className="wanderer-scar scar-stitches" stroke="#7a1f2b" strokeWidth="0.35">
          <path d={`M${g.stitch[0]} ${g.stitch[1]} h3.4`} />
          <path d={`M${g.stitch[0] + 0.6} ${g.stitch[1] - 0.6} v1.2 M${g.stitch[0] + 1.7} ${g.stitch[1] - 0.6} v1.2 M${g.stitch[0] + 2.8} ${g.stitch[1] - 0.6} v1.2`} />
        </g>
      )}
      {on.includes("sling") && (
        <rect className="wanderer-scar scar-sling" x={g.arm[0]} y={g.arm[1]} width="3.4" height="3"
          rx="0.6" fill="#f4f4f4" stroke="#9aa" strokeWidth="0.3" />
      )}
    </svg>
  );
}

// the tiny suitcase for the "I'm leaving" ending
function Suitcase() {
  return (
    <svg className="wanderer-suitcase" width="16" height="14" viewBox="0 0 16 14" shapeRendering="crispEdges" aria-hidden="true">
      <rect x="5.5" y="0.5" width="5" height="3" rx="1" fill="none" stroke="#5a3a1e" strokeWidth="1" />
      <rect x="1" y="3" width="14" height="10" rx="1.5" fill="#a0612a" stroke="#5a3a1e" strokeWidth="0.8" />
      <rect x="1" y="7" width="14" height="1.2" fill="#5a3a1e" />
    </svg>
  );
}

// a snack on the floor (phase 32) — a little pixel cookie
function Snack({ x }) {
  return (
    <svg className="wanderer-snack" style={{ left: `${x}%` }} width="14" height="14" viewBox="0 0 14 14" shapeRendering="crispEdges" aria-hidden="true">
      <circle cx="7" cy="7" r="6" fill="#c98b4a" stroke="#7a4f22" strokeWidth="0.8" shapeRendering="geometricPrecision" />
      <rect x="4" y="4" width="1.6" height="1.6" fill="#3b2412" />
      <rect x="8" y="5" width="1.6" height="1.6" fill="#3b2412" />
      <rect x="6" y="8.4" width="1.6" height="1.6" fill="#3b2412" />
    </svg>
  );
}

// Overlay particles for the crash-out beats (and, phase 32, hearts and snack crumbs). Positions are random per mount so every crash
// looks a little different; all motion is CSS (index.css, .wfx-*).
function Particles({ kind }) {
  const [items] = useState(() => {
    if (kind === "glitch") {
      return Array.from({ length: 14 }, (_, i) => ({
        i, ch: GLYPHS[randInt(0, GLYPHS.length - 1)],
        x: rand(-70, 110), y: rand(-20, 100), d: rand(0, 0.5),
      }));
    }
    if (kind === "stars") {
      return Array.from({ length: 4 }, (_, i) => ({ i, ch: "✦", x: 20 + i * 20, y: -12, d: i * 0.18 }));
    }
    if (kind === "hearts") {
      return Array.from({ length: 3 }, (_, i) => ({ i, ch: "♥", x: rand(10, 80), y: rand(-20, 10), d: i * 0.12 }));
    }
    if (kind === "crumbs") {
      return Array.from({ length: 6 }, (_, i) => ({ i, ch: i % 2 ? "·" : "▪", x: rand(20, 90), y: rand(40, 70), d: rand(0, 0.5) }));
    }
    return Array.from({ length: 8 }, (_, i) => ({
      i, ch: i % 2 ? "·" : "∘", x: rand(-30, 130), y: rand(60, 100), d: rand(0, 0.4),
    }));
  });
  return (
    <div className={`wfx wfx-${kind}`}>
      {items.map((it) => (
        <span
          key={it.i}
          className="wfx-p"
          style={{ left: `${it.x}%`, top: `${it.y}%`, animationDelay: `${it.d}s` }}
        >
          {it.ch}
        </span>
      ))}
    </div>
  );
}
