import { normalizeConfig } from "./config";
import { Base, Outfit, Hair, Accessory, Face } from "./parts";
import "./sprites.css";

// The 8 animation states. Each maps to a CSS class (s-<state>) on the <svg>; sprites.css
// drives the motion (all off under prefers-reduced-motion). States:
//   neutral  — idle bob
//   thinking — bob + a "..." bubble (this player is typing)
//   excited  — bounce
//   sad      — head tilt down, drooped arms
//   mad      — shake + red flash + "grr"
//   victorious — jump loop + winner glow + trophy
//   defeated — head fully down, dimmed
//   confused — sway + "?"
const SPRITE_STATES = [
  "neutral", "thinking", "excited", "sad",
  "mad", "victorious", "defeated", "confused",
];

// Layered chibi character. Pass an explicit `config` (phase-12 creator) or let it derive
// deterministically from `name` — which also gives the AI a plausible character for free.
export default function CharacterSprite({ config, name, state = "neutral", size = 56, title }) {
  const cfg = normalizeConfig(config, name);
  const st = SPRITE_STATES.includes(state) ? state : "neutral";
  const label = title || `${name || "player"} — ${st}`;

  return (
    <svg
      className={`sprite s-${st}`}
      width={size}
      height={size * (40 / 32)}
      viewBox="0 0 32 40"
      shapeRendering="crispEdges"
      role="img"
      aria-label={label}
    >
      <title>{label}</title>

      {/* sway wraps everything (confused) */}
      <g className="sp-sway">
        {/* shake wraps everything (mad) */}
        <g className="sp-shake">
          {/* vertical bob / bounce / jump */}
          <g className="sp-bob">
            {/* body: torso + arms (arms droop on sad/defeated) */}
            <g className="sp-body">
              <Base base={cfg.base} />
              <Outfit outfit={cfg.outfit} base={cfg.base} />
            </g>
            {/* head group tilts for sad/defeated */}
            <g className="sp-head">
              <Hair hair={cfg.hair} />
              <Face state={st} />
              <Accessory accessory={cfg.accessory} />
              {/* red anger flash over the head */}
              {st === "mad" && <rect className="sp-flash" x="6.5" y="2" width="19" height="17" rx="6.5" fill="#ff3b4e" />}
            </g>
          </g>
        </g>
      </g>

      <Marks state={st} />
    </svg>
  );
}

// State marks drawn in the top-right/above the head — thinking dots, grr, ?, trophy.
function Marks({ state }) {
  if (state === "thinking") {
    return (
      <g className="sp-think">
        <rect x="20.5" y="0.5" width="10.5" height="6" rx="3" fill="#0a0f0a" stroke="#33ff66" strokeWidth="0.5" />
        <circle className="d1" cx="23.5" cy="3.5" r="0.9" fill="#33ff66" />
        <circle className="d2" cx="26" cy="3.5" r="0.9" fill="#33ff66" />
        <circle className="d3" cx="28.5" cy="3.5" r="0.9" fill="#33ff66" />
      </g>
    );
  }
  if (state === "confused") {
    return (
      <text className="sp-mark q" x="26" y="6" fontSize="7" fill="#ffb300" fontFamily="monospace" fontWeight="bold">?</text>
    );
  }
  if (state === "mad") {
    return (
      <text className="sp-mark grr" x="22" y="5" fontSize="4.2" fill="#ff3b4e" fontFamily="monospace" fontWeight="bold">grr</text>
    );
  }
  if (state === "victorious") {
    // little trophy above the head
    return (
      <g className="sp-trophy" shapeRendering="geometricPrecision">
        <path d="M13.5 -0.5 h5 v2 a2.5 2.5 0 0 1 -5 0 Z" fill="#f2c94c" stroke="#b8901f" strokeWidth="0.3" />
        <rect x="15.4" y="1.4" width="1.2" height="1.6" fill="#b8901f" />
        <rect x="14.2" y="3" width="3.6" height="1" rx="0.4" fill="#f2c94c" stroke="#b8901f" strokeWidth="0.2" />
      </g>
    );
  }
  return null;
}
