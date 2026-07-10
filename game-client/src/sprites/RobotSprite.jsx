import "./sprites.css";

// The game's mascot — "the AI" — as a deliberately synthetic little robot, drawn in the
// same layered SVG pixel style as the player chibis (crispEdges grid). Boxy head, a twitchy
// antenna, and a single glowing LED eye that blinks. Motion is all CSS (walk shuffle, LED
// blink, antenna pulse) and disabled under prefers-reduced-motion; the head pivot (looking
// around) is driven by the `looking` prop from HomeRobot. Purely decorative — it is NOT a
// player sprite and carries no character config.
//
// props:
//   size     — pixel height basis
//   walking  — true to run the leg/arm shuffle
//   looking  — -1 / 0 / 1, head glances left / center / right
export default function RobotSprite({ size = 64, walking = false, looking = 0 }) {
  const metal = "#9fb2b8";
  const metalDk = "#5f6f75";
  const bolt = "#3a464b";
  const led = "#33ff66";

  return (
    <svg
      className={`robot${walking ? " walk" : ""}`}
      width={size * (28 / 40)}
      height={size}
      viewBox="0 0 28 40"
      shapeRendering="crispEdges"
      role="img"
      aria-label="the AI robot mascot"
    >
      <title>the AI</title>

      {/* legs — shuffle when walking */}
      <g>
        <rect className="rb-leg-l" x="8" y="31" width="4" height="7" rx="1" fill={metalDk} stroke={bolt} strokeWidth="0.4" />
        <rect className="rb-leg-r" x="16" y="31" width="4" height="7" rx="1" fill={metalDk} stroke={bolt} strokeWidth="0.4" />
        {/* feet */}
        <rect className="rb-leg-l" x="7" y="37.5" width="6" height="2" rx="0.8" fill={bolt} />
        <rect className="rb-leg-r" x="15" y="37.5" width="6" height="2" rx="0.8" fill={bolt} />
      </g>

      {/* arms — swing opposite the legs */}
      <rect className="rb-arm-l" x="3.5" y="19" width="3" height="10" rx="1.3" fill={metal} stroke={bolt} strokeWidth="0.4" />
      <rect className="rb-arm-r" x="21.5" y="19" width="3" height="10" rx="1.3" fill={metal} stroke={bolt} strokeWidth="0.4" />

      {/* body bobs a touch while walking */}
      <g className="rb-body">
        {/* torso */}
        <rect x="6" y="17.5" width="16" height="14" rx="2.5" fill={metal} stroke={bolt} strokeWidth="0.6" />
        {/* chest panel + status LEDs */}
        <rect x="9.5" y="21" width="9" height="6" rx="1" fill="#2b3438" stroke={bolt} strokeWidth="0.4" />
        <circle className="rb-chest d1" cx="11.5" cy="24" r="0.9" fill={led} />
        <circle className="rb-chest d2" cx="14" cy="24" r="0.9" fill="#ffb300" />
        <circle className="rb-chest d3" cx="16.5" cy="24" r="0.9" fill="#2e86de" />
        {/* neck */}
        <rect x="12" y="15" width="4" height="3" fill={metalDk} />

        {/* head group pivots to "look around" */}
        <g className="rb-head" style={{ transform: `translateX(${looking * 1.4}px) rotate(${looking * 3}deg)` }}>
          {/* antenna */}
          <rect x="13.6" y="-1.5" width="0.8" height="6" fill={metalDk} />
          <circle className="rb-ball" cx="14" cy="-1.8" r="1.6" fill="#ffb300" />
          {/* head shell */}
          <rect x="5" y="4" width="18" height="12" rx="2.5" fill={metal} stroke={bolt} strokeWidth="0.6" />
          {/* ear bolts */}
          <rect x="3.6" y="8" width="1.8" height="4" rx="0.6" fill={metalDk} />
          <rect x="22.6" y="8" width="1.8" height="4" rx="0.6" fill={metalDk} />
          {/* visor screen */}
          <rect x="7.5" y="6.8" width="13" height="5.2" rx="1.4" fill="#0c1512" stroke={bolt} strokeWidth="0.5" />
          {/* the single LED eye — blinks */}
          <rect className="rb-led" x="11.5" y="8" width="5" height="2.6" rx="1.3" fill={led} />
          {/* mouth grille */}
          <g stroke={bolt} strokeWidth="0.6">
            <path d="M10 14 V15.2 M12.5 14 V15.2 M15.5 14 V15.2 M18 14 V15.2" />
          </g>
        </g>
      </g>
    </svg>
  );
}
