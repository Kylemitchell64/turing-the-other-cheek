import { useEffect, useRef, useState } from "react";
import RobotSprite from "../sprites/RobotSprite";
import CharacterSprite from "../sprites/CharacterSprite";
import { useAuth } from "../auth/AuthContext";
import { api } from "../api/client";

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
// prefers-reduced-motion: a single static mascot — no wander, no fall, no interval.
const rand = (lo, hi) => lo + Math.random() * (hi - lo);
const randInt = (lo, hi) => Math.floor(rand(lo, hi + 1));
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

export default function MenuWanderer() {
  const { token, user } = useAuth();
  const username = user?.displayName || user?.unique_name || "player";

  const reduced =
    typeof window !== "undefined" &&
    window.matchMedia &&
    window.matchMedia("(prefers-reduced-motion: reduce)").matches;

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

  useEffect(() => {
    if (reduced) return;
    const id = setInterval(() => {
      setPose((p) => {
        const ph = phase.current;
        ph.ticks -= 1;
        if (p.walking) {
          let pos = p.pos + 0.32 * p.dir;
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

  // ---- scroll gravity (phase 27) ----
  // The mascot rests pinned to the viewport bottom (offY 0). A scroll kicks offY away from
  // home — up when you scroll down (so it then FALLS back down), down when you scroll up —
  // and a spring pulls it home. Vertical speed stretches it thin mid-air; crossing home with
  // speed lands a squash. All applied straight to the DOM node so we never re-render per frame.
  const bodyRef = useRef(null);
  const phys = useRef({ offY: 0, velY: 0, squash: 0, raf: 0, lastY: 0 });

  useEffect(() => {
    if (reduced) return;
    const p = phys.current;
    p.lastY = typeof window !== "undefined" ? window.scrollY || 0 : 0;

    const apply = () => {
      const el = bodyRef.current;
      if (!el) return;
      const stretch = clamp(Math.abs(p.velY) * 0.014, 0, 0.32); // thin + tall while airborne
      const sy = clamp(1 + stretch - p.squash, 0.62, 1.4);
      const sx = clamp(1 - stretch * 0.6 + p.squash * 0.85, 0.7, 1.4);
      el.style.transform =
        `translateY(${p.offY.toFixed(2)}px) scale(${sx.toFixed(3)}, ${sy.toFixed(3)})`;
    };

    const frame = () => {
      const before = p.offY;
      p.velY += -0.16 * p.offY; // spring toward home
      p.velY *= 0.74; // damping
      p.offY += p.velY;
      // crossed the resting spot with some speed => it just landed/settled: squash a bit,
      // capped so a fast flick can't flatten it into a pancake.
      if (before !== 0 && Math.sign(before) !== Math.sign(p.offY) && Math.abs(p.velY) > 0.4) {
        p.squash = clamp(p.squash + Math.abs(p.velY) * 0.03, 0, 0.3);
      }
      p.squash *= 0.82;
      apply();
      if (Math.abs(p.offY) > 0.15 || Math.abs(p.velY) > 0.15 || p.squash > 0.01) {
        p.raf = requestAnimationFrame(frame);
      } else {
        p.offY = 0; p.velY = 0; p.squash = 0;
        apply();
        p.raf = 0;
      }
    };

    const kick = () => { if (!p.raf) p.raf = requestAnimationFrame(frame); };

    const onScroll = () => {
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
  }, [reduced]);

  const style = {
    left: `${pose.pos}%`,
    transform: `scaleX(${pose.dir})`,
  };

  return (
    <div className="home-robot" aria-hidden="true">
      <div className="home-robot-walker" style={style}>
        <div className="wanderer-body" ref={bodyRef}>
          {character ? (
            <CharacterSprite
              name={username}
              config={character}
              state="neutral"
              size={50}
              look={reduced ? null : { dx: pose.look * 1.1, dy: pose.look !== 0 ? 0.35 : 0 }}
              blink={blink}
            />
          ) : (
            <RobotSprite size={60} walking={!reduced && pose.walking} looking={pose.look} />
          )}
        </div>
      </div>
    </div>
  );
}
