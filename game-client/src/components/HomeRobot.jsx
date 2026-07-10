import { useEffect, useRef, useState } from "react";
import RobotSprite from "../sprites/RobotSprite";

// The mascot robot wandering along the bottom of the Home screen (phase 21). It walks
// left-right, flips at the edges, and every so often stops to look around before carrying
// on — a subtle bit of life behind/below the menu, never in the way. Slow cadence.
// prefers-reduced-motion: a single static robot, no wander, no interval.
const rand = (lo, hi) => lo + Math.random() * (hi - lo);
const randInt = (lo, hi) => Math.floor(rand(lo, hi + 1));

export default function HomeRobot() {
  const reduced =
    typeof window !== "undefined" &&
    window.matchMedia &&
    window.matchMedia("(prefers-reduced-motion: reduce)").matches;

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

  // A static robot for reduced-motion: parked, facing right, no head turn.
  const style = {
    left: `${pose.pos}%`,
    transform: `scaleX(${pose.dir})`,
  };

  return (
    <div className="home-robot" aria-hidden="true">
      <div className="home-robot-walker" style={style}>
        <RobotSprite size={60} walking={!reduced && pose.walking} looking={pose.look} />
      </div>
    </div>
  );
}
