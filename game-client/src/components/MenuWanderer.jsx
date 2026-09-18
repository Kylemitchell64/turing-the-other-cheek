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
const rand = (lo, hi) => lo + Math.random() * (hi - lo);
const randInt = (lo, hi) => Math.floor(rand(lo, hi + 1));
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

const NUDGE_WINDOW_MS = 30_000;
const NUDGE_LIMIT = 10; // the 11th nudge inside the window triggers the crash-out

// what it mutters after each nudge, indexed by how many it's taken lately
const MUTTERS = ["", "?", "hey", "hm.", "stop", "quit it", "seriously.", "STOP", "grr", "!!!", "LAST WARNING"];
const GLYPHS = "▓▒░█▚▞╳@#%&*<>/\\|=+~";

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

  // ---- phase 28: temper + crash-out state ----
  // mode: idle | held | glitch | flee | fall | down | dust. Anything but idle suspends the
  // wander loop; the crash sequence drives pos/offY itself.
  const mode = useRef("idle");
  const nudges = useRef([]); // timestamps of recent pokes/drops
  const [mood, setMood] = useState("calm");
  const [mutter, setMutter] = useState("");
  const [fx, setFx] = useState(null); // "glitch" | "stars" | "dust" | null — overlay particles
  const [held, setHeld] = useState(false);
  const [dropping, setDropping] = useState(false); // released, still falling to the floor
  const [crashStage, setCrashStage] = useState(null); // mirrors mode for CSS classes
  const timers = useRef([]);
  const after = useCallback((ms, fn) => { const t = setTimeout(fn, ms); timers.current.push(t); return t; }, []);
  useEffect(() => () => timers.current.forEach(clearTimeout), []);

  const fleeTarget = useRef(null); // { to, dir } while sprinting off-screen

  useEffect(() => {
    if (reduced) return;
    const id = setInterval(() => {
      // temper cools as nudges age out of the window
      const now = Date.now();
      const before = nudges.current.length;
      nudges.current = nudges.current.filter((t) => now - t < NUDGE_WINDOW_MS);
      if (nudges.current.length !== before) setMood(moodFor(nudges.current.length));

      setPose((p) => {
        if (mode.current === "flee" && fleeTarget.current) {
          const { to, dir } = fleeTarget.current;
          const pos = p.pos + 2.2 * dir;
          const done = dir > 0 ? pos >= to : pos <= to;
          return { pos: done ? to : pos, dir, walking: true, look: 0 };
        }
        if (mode.current !== "idle") return p;

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
    }, 60);
    return () => clearInterval(id);
  }, [reduced]);

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

  // ---- vertical physics (phase 27 spring, phase 28 gravity) ----
  // The mascot rests pinned to the viewport bottom (offY 0). A scroll kicks offY away from
  // home and a spring pulls it back. Being dropped or falling from the portal switches to
  // plain gravity with a floor at 0 and a bounce. Vertical speed stretches it thin mid-air;
  // landing squashes it. All applied straight to the DOM node — no re-render per frame.
  const bodyRef = useRef(null);
  const phys = useRef({ offY: 0, velY: 0, squash: 0, rot: 0, spin: 0, gravity: false, raf: 0, lastY: 0, onLand: null });

  const apply = useCallback(() => {
    const el = bodyRef.current;
    const p = phys.current;
    if (!el) return;
    const stretch = clamp(Math.abs(p.velY) * 0.014, 0, 0.32); // thin + tall while airborne
    const sy = clamp(1 + stretch - p.squash, 0.62, 1.4);
    const sx = clamp(1 - stretch * 0.6 + p.squash * 0.85, 0.7, 1.4);
    el.style.transform =
      `translateY(${p.offY.toFixed(2)}px) rotate(${p.rot.toFixed(1)}deg) scale(${sx.toFixed(3)}, ${sy.toFixed(3)})`;
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
        if (p.offY >= 0) {
          // hit the floor
          const impact = p.velY;
          p.offY = 0;
          p.squash = clamp(p.squash + impact * 0.035, 0, 0.42);
          if (impact > 9) {
            p.velY = -impact * 0.28; // little bounce
          } else {
            p.velY = 0; p.gravity = false; p.spin = 0; p.rot = 0;
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

  const crashOut = useCallback(() => {
    // 1) glitch out
    mode.current = "glitch";
    setHeld(false);
    setCrashStage("glitch");
    setFx("glitch");
    setMutter("");
    sfxBus.emit("crash");
    after(950, () => {
      // 2) sprint off whichever edge is closer
      setFx(null);
      setCrashStage("flee");
      setPose((p) => {
        const dir = p.pos < 45 ? -1 : 1;
        fleeTarget.current = { to: dir < 0 ? -16 : 106, dir };
        return { ...p, dir, walking: true, look: 0 };
      });
      mode.current = "flee";
    });
  }, [after]);

  // flee finished => portal drop from the top
  useEffect(() => {
    if (mode.current !== "flee" || !fleeTarget.current) return;
    const { to } = fleeTarget.current;
    if (pose.pos !== to) return;
    fleeTarget.current = null;
    mode.current = "fall";
    setCrashStage("fall");
    const p = phys.current;
    after(420, () => {
      setPose({ pos: randInt(12, 78), dir: 1, walking: false, look: 0 });
      p.offY = -(window.innerHeight || 800) - 40;
      p.velY = 0;
      p.rot = 0;
      p.spin = rand(3, 6) * (Math.random() < 0.5 ? -1 : 1);
      p.gravity = true;
      p.onLand = () => {
        // 3) faceplant
        mode.current = "down";
        setCrashStage("down");
        setFx("stars");
        sfxBus.emit("thud");
        after(2700, () => {
          // 4) up (slowly), dust off, clean slate
          mode.current = "dust";
          setCrashStage("dust");
          setFx("dust");
          sfxBus.emit("dust");
          nudges.current = [];
          setMood("calm");
          after(1700, () => {
            setFx(null);
            setCrashStage(null);
            phase.current = { ticks: randInt(30, 60) };
            mode.current = "idle";
            setPose((q) => ({ ...q, walking: false, look: 0 }));
          });
        });
      };
      kick();
    });
  }, [pose.pos, after, kick]);

  const nudge = useCallback((pointerX) => {
    if (mode.current !== "idle") return;
    const now = Date.now();
    nudges.current = nudges.current.filter((t) => now - t < NUDGE_WINDOW_MS);
    nudges.current.push(now);
    const n = nudges.current.length;
    if (n > NUDGE_LIMIT) { crashOut(); return; }
    setMood(moodFor(n));
    say(MUTTERS[n] || "!!!");
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

  // ---- pointer: click = poke, drag = pick up & drop ----
  const drag = useRef(null);
  const walkerRef = useRef(null);

  const onPointerDown = (e) => {
    if (reduced || mode.current !== "idle") return;
    e.preventDefault();
    try { walkerRef.current?.setPointerCapture?.(e.pointerId); } catch { /* synthetic / already-released pointer */ }
    drag.current = { id: e.pointerId, x0: e.clientX, y0: e.clientY, moved: false, startPos: pose.pos };
  };
  const onPointerMove = (e) => {
    const d = drag.current;
    if (!d || d.id !== e.pointerId) return;
    const dx = e.clientX - d.x0;
    const dy = e.clientY - d.y0;
    if (!d.moved) {
      if (Math.hypot(dx, dy) < 6) return;
      d.moved = true;
      mode.current = "held";
      setHeld(true);
      sfxBus.emit("grab");
      phys.current.gravity = false;
      phys.current.velY = 0;
      setPose((q) => ({ ...q, walking: false, look: 0 }));
    }
    const vw = window.innerWidth || 1;
    const pos = clamp(d.startPos + (dx / vw) * 100, -4, 92);
    setPose((q) => ({ ...q, pos }));
    const p = phys.current;
    p.offY = Math.min(0, dy - 4); // lifted straight up under the pointer
    p.rot = -clamp(dx * 0.08, -14, 14);
    apply();
  };
  const onPointerUp = (e) => {
    const d = drag.current;
    if (!d || d.id !== e.pointerId) return;
    drag.current = null;
    try { walkerRef.current?.releasePointerCapture?.(e.pointerId); } catch { /* ditto */ }
    if (!d.moved) { nudge(e.clientX); return; }
    // dropped: fall to the floor, land with a squash, then count it as a nudge. It stays
    // "dropping" (layer lifted above the panels) until it actually touches down.
    setHeld(false);
    setDropping(true);
    const p = phys.current;
    p.spin = 0;
    p.rot = 0;
    p.gravity = true;
    p.velY = 0;
    p.onLand = () => {
      mode.current = "idle";
      setDropping(false);
      sfxBus.emit("land");
      nudge(null);
    };
    kick();
    // if it was dropped basically at floor level, land right away
    if (p.offY >= -1) { p.gravity = false; p.offY = 0; const cb = p.onLand; p.onLand = null; cb?.(0); }
  };

  const style = {
    left: `${pose.pos}%`,
    transform: `scaleX(${pose.dir})`,
  };

  // sprite emotion for the character mascot; the robot gets the same via CSS classes
  let spriteState = "neutral";
  if (crashStage === "glitch") spriteState = "confused";
  else if (crashStage === "flee") spriteState = "mad";
  else if (crashStage === "fall") spriteState = "confused";
  else if (crashStage === "down") spriteState = "defeated";
  else if (crashStage === "dust") spriteState = "excited";
  else if (held) spriteState = "confused";
  else if (mood === "angry") spriteState = "mad";
  else if (mood === "irritated") spriteState = "sad";
  else if (mood === "annoyed") spriteState = "confused";

  const bodyClass = [
    "wanderer-body",
    `mood-${mood}`,
    held ? "held" : "",
    crashStage ? `crash-${crashStage}` : "",
  ].filter(Boolean).join(" ");

  const interactive = !reduced;

  return (
    <div className={`home-robot${crashStage ? " crashing" : ""}${held || dropping || crashStage ? " active" : ""}`} aria-hidden="true">
      <div
        className={`home-robot-walker${interactive ? " grabbable" : ""}${held ? " grabbing" : ""}`}
        style={style}
        ref={walkerRef}
        onPointerDown={interactive ? onPointerDown : undefined}
        onPointerMove={interactive ? onPointerMove : undefined}
        onPointerUp={interactive ? onPointerUp : undefined}
        onPointerCancel={interactive ? onPointerUp : undefined}
      >
        <div className={bodyClass} ref={bodyRef}>
          {character ? (
            <CharacterSprite
              name={username}
              config={character}
              state={spriteState}
              size={50 * scale}
              look={reduced ? null : { dx: pose.look * 1.1, dy: pose.look !== 0 ? 0.35 : 0 }}
              blink={blink}
            />
          ) : (
            <RobotSprite size={60 * scale} walking={!reduced && pose.walking} looking={pose.look} />
          )}
          {fx && <Particles kind={fx} />}
        </div>
        {/* mutter bubble is a child of the walker so it flips back to readable with dir */}
        {mutter && (
          <div className="wanderer-mutter" style={{ transform: `translateX(-50%) scaleX(${pose.dir})` }}>
            {mutter}
          </div>
        )}
      </div>
    </div>
  );
}

// Overlay particles for the crash-out beats. Positions are random per mount so every crash
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
