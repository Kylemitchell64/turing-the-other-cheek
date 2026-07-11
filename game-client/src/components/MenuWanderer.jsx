import { useEffect, useRef, useState } from "react";
import RobotSprite from "../sprites/RobotSprite";
import CharacterSprite from "../sprites/CharacterSprite";
import { useAuth } from "../auth/AuthContext";
import { api } from "../api/client";

// The menu mascot (phase 25, grown out of the phase-21 HomeRobot). It wanders along the
// bottom of every menu screen, behind the panels: walks left-right, flips at the edges,
// stops now and then to look around. If you're signed in with a saved character it's YOU
// down there; otherwise (login screen, or nobody's saved a look yet) it's the robot.
// prefers-reduced-motion: a single static mascot, no wander, no interval.
const rand = (lo, hi) => lo + Math.random() * (hi - lo);
const randInt = (lo, hi) => Math.floor(rand(lo, hi + 1));

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

  const style = {
    left: `${pose.pos}%`,
    transform: `scaleX(${pose.dir})`,
  };

  return (
    <div className="home-robot" aria-hidden="true">
      <div className="home-robot-walker" style={style}>
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
  );
}
