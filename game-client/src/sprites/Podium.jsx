import CharacterSprite from "./CharacterSprite";

// One podium: a chibi character standing behind a little wooden crate, name label in
// terminal green underneath, and the player's remaining fake-out tokens as pips on the
// crate front. Used in a wrapping row on the Game screen (one per roster member).
export default function Podium({ name, config, state = "neutral", isMe = false, tokens = 3, size = 58 }) {
  const stageH = Math.round(size * 1.25);
  const crateW = Math.round(size + 14);
  return (
    <div className={`podium${isMe ? " me" : ""}${state === "defeated" ? " downed" : ""}`}>
      <div className="podium-stage" style={{ width: crateW, height: stageH }}>
        <div className="podium-char" style={{ bottom: Math.round(size * 0.28) }}>
          <CharacterSprite name={name} config={config} state={state} size={size} />
        </div>
        <Crate tokens={tokens} width={crateW} />
      </div>
      <div className="podium-name" title={name}>
        {name}{isMe ? " (you)" : ""}
      </div>
    </div>
  );
}

// Wooden pixel crate. Token pips sit on the front slat.
function Crate({ tokens, width }) {
  const h = Math.round((width * 24) / 44);
  return (
    <svg className="podium-crate" width={width} height={h} viewBox="0 0 44 24" shapeRendering="crispEdges" aria-hidden="true">
      {/* box body */}
      <rect x="2" y="4" width="40" height="19" rx="1.5" fill="#a9713e" stroke="#5f3a1e" strokeWidth="1" />
      {/* lid rim */}
      <rect x="1" y="2.5" width="42" height="4" rx="1" fill="#c58e50" stroke="#5f3a1e" strokeWidth="0.8" />
      {/* plank seams + diagonal brace for that crate look */}
      <path d="M22 6.5 V23 M6 6.5 L38 21 M38 6.5 L6 21" stroke="#7a4a26" strokeWidth="0.8" fill="none" opacity="0.7" />
      {/* token pips */}
      {[0, 1, 2].map((i) => (
        <rect
          key={i}
          className={i < tokens ? "crate-pip on" : "crate-pip"}
          x={15.5 + i * 5}
          y="9.5"
          width="3"
          height="3"
          rx="0.6"
        />
      ))}
    </svg>
  );
}
