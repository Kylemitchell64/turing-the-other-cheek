// Hand-authored SVG layers for the chibi character, all on a shared 32x40 pixel grid
// (the crate/podium is drawn separately by Podium and covers the waist-down). Parts are
// built from rects + a few paths on integer coords; the sprite renders with
// shape-rendering:crispEdges so it stays pixel-sharp. Faces use small paths with
// geometricPrecision so smiles/frowns read as curves.

// ---- palettes ----

// 8 base skin/body tones (pale-leaning, plus a mint and an android-grey for variety).
export const SKINS = [
  "#f3d6b6", "#e8b98f", "#cf9366", "#a96b43",
  "#754c33", "#f8e3cd", "#a6e2bd", "#cfd8dd",
];

// 10 hair colours, indexed with the hair style.
const HAIR_COLORS = [
  "#2b2b2b", "#5a3825", "#b5651d", "#e0b44a", "#d7d7d7",
  "#c0392b", "#6c5ce7", "#2e86de", "#27ae60", "#e84393",
];

// 10 outfit colours + a matching darker sleeve shade.
const OUTFITS = [
  "#2e86de", "#c0392b", "#27ae60", "#8e44ad", "#f39c12",
  "#16a085", "#3b5168", "#d35400", "#b9c0c6", "#e84393",
];
const SLEEVES = [
  "#1f5fa8", "#8f2b20", "#1c8049", "#653381", "#b9760c",
  "#0f6f5c", "#28394b", "#9c3d00", "#8b9298", "#ad2b6c",
];

const OUTLINE = "#0a0f0a";

// ---- base: head + neck + torso frame + arms ----
// The torso itself is coloured by the outfit layer; the base draws skin + arms only.
export function Base({ base }) {
  const skin = SKINS[base % SKINS.length];
  return (
    <g>
      {/* arms (behind torso) — skin, short sleeves added by the outfit */}
      <rect x="4.5" y="21" width="3" height="11" rx="1.3" fill={skin} stroke={OUTLINE} strokeWidth="0.4" />
      <rect x="24.5" y="21" width="3" height="11" rx="1.3" fill={skin} stroke={OUTLINE} strokeWidth="0.4" />
      {/* neck */}
      <rect x="13" y="17.5" width="6" height="3.5" fill={skin} />
      {/* head — big round chibi head */}
      <rect x="6.5" y="2" width="19" height="17" rx="6.5" fill={skin} stroke={OUTLINE} strokeWidth="0.5" />
      {/* ears */}
      <rect x="5.4" y="9.5" width="2.2" height="4" rx="1" fill={skin} stroke={OUTLINE} strokeWidth="0.3" />
      <rect x="24.4" y="9.5" width="2.2" height="4" rx="1" fill={skin} stroke={OUTLINE} strokeWidth="0.3" />
    </g>
  );
}

// ---- outfit: torso shirt + short sleeves + collar ----
export function Outfit({ outfit, base }) {
  const c = OUTFITS[outfit % OUTFITS.length];
  const s = SLEEVES[outfit % SLEEVES.length];
  const skin = SKINS[base % SKINS.length];
  return (
    <g>
      {/* torso */}
      <rect x="8" y="20" width="16" height="19" rx="3" fill={c} stroke={OUTLINE} strokeWidth="0.5" />
      {/* short sleeves over the arm tops */}
      <rect x="4.3" y="20.5" width="4" height="4.5" rx="1.4" fill={s} />
      <rect x="23.7" y="20.5" width="4" height="4.5" rx="1.4" fill={s} />
      {/* collar notch */}
      <path d="M13 20 L16 22.5 L19 20 Z" fill={s} />
      {/* hands */}
      <rect x="4.6" y="31.4" width="2.8" height="2.8" rx="1" fill={skin} stroke={OUTLINE} strokeWidth="0.3" />
      <rect x="24.6" y="31.4" width="2.8" height="2.8" rx="1" fill={skin} stroke={OUTLINE} strokeWidth="0.3" />
    </g>
  );
}

// ---- hair: 10 styles, each with its own colour ----
export function Hair({ hair }) {
  const i = hair % 10;
  const col = HAIR_COLORS[i];
  const stroke = { stroke: OUTLINE, strokeWidth: 0.3 };
  switch (i) {
    case 0: // short flat cap
      return <rect x="6.2" y="1.4" width="19.6" height="6" rx="5" fill={col} {...stroke} />;
    case 1: // spiky
      return (
        <g fill={col} {...stroke}>
          <rect x="7" y="3" width="18" height="3.5" rx="1.5" />
          <path d="M7 4 L9 -1 L11.5 4 L14 -1.5 L16.5 4 L19 -1 L21.5 4 L24 0 L25 4 Z" />
        </g>
      );
    case 2: // bowl cut
      return <rect x="5.6" y="0.8" width="20.8" height="9.5" rx="6" fill={col} {...stroke} />;
    case 3: // long, framing the face
      return (
        <g fill={col} {...stroke}>
          <rect x="6.2" y="1.4" width="19.6" height="6" rx="5" />
          <rect x="5.6" y="5" width="3" height="13" rx="1.3" />
          <rect x="23.4" y="5" width="3" height="13" rx="1.3" />
        </g>
      );
    case 4: // top bun
      return (
        <g fill={col} {...stroke}>
          <circle cx="16" cy="1.4" r="3" />
          <rect x="6.6" y="2" width="18.8" height="5" rx="4.5" />
        </g>
      );
    case 5: // mohawk
      return (
        <g fill={col} {...stroke}>
          <rect x="14.3" y="-1.5" width="3.4" height="8.5" rx="1.2" />
          <rect x="7" y="4.5" width="18" height="2.5" rx="1.2" />
        </g>
      );
    case 6: // buzz
      return <rect x="7" y="2.3" width="18" height="3" rx="1.5" fill={col} opacity="0.9" {...stroke} />;
    case 7: // curly
      return (
        <g fill={col} {...stroke}>
          <circle cx="9" cy="3.6" r="2.6" />
          <circle cx="13" cy="2.4" r="2.7" />
          <circle cx="16.5" cy="2.1" r="2.8" />
          <circle cx="20" cy="2.4" r="2.7" />
          <circle cx="23.5" cy="3.6" r="2.6" />
          <rect x="7.5" y="3.5" width="17" height="3" rx="1.5" />
        </g>
      );
    case 8: // side swoop
      return (
        <g fill={col} {...stroke}>
          <path d="M6.5 6.5 Q7 1 16 1.4 Q26 1.8 25.5 5 Q20 3 13 4.5 Q9 5.5 8 7 Z" />
          <rect x="6.4" y="2" width="7" height="6" rx="2.5" />
        </g>
      );
    case 9: // pigtails
      return (
        <g fill={col} {...stroke}>
          <rect x="6.6" y="1.6" width="18.8" height="5.5" rx="4.5" />
          <circle cx="6.4" cy="11" r="3.1" />
          <circle cx="25.6" cy="11" r="3.1" />
        </g>
      );
    default:
      return null;
  }
}

// ---- accessory: 6 options (0..5) ----
export function Accessory({ accessory }) {
  if (accessory == null) return null;
  const dark = "#14181c";
  switch (accessory % 6) {
    case 0: // glasses
      return (
        <g fill="none" stroke={dark} strokeWidth="0.7">
          <rect x="10.4" y="10" width="4.2" height="4" rx="1.2" />
          <rect x="17.4" y="10" width="4.2" height="4" rx="1.2" />
          <path d="M14.6 11.6 L17.4 11.6" />
        </g>
      );
    case 1: // sunglasses
      return (
        <g fill={dark} stroke={dark} strokeWidth="0.5">
          <rect x="10.2" y="10" width="4.6" height="4" rx="1.2" />
          <rect x="17.2" y="10" width="4.6" height="4" rx="1.2" />
          <rect x="14.6" y="11.4" width="2.8" height="0.8" />
        </g>
      );
    case 2: // headband
      return <rect x="6.4" y="6.4" width="19.2" height="2.4" rx="1" fill="#c0392b" stroke={OUTLINE} strokeWidth="0.3" />;
    case 3: // earrings
      return (
        <g fill="#f2c94c" stroke="#b8901f" strokeWidth="0.2">
          <circle cx="6.4" cy="13.6" r="1" />
          <circle cx="25.6" cy="13.6" r="1" />
        </g>
      );
    case 4: // bowtie at the collar
      return (
        <g fill="#c0392b" stroke={OUTLINE} strokeWidth="0.3">
          <path d="M16 21.4 L12.5 19.8 L12.5 23 Z" />
          <path d="M16 21.4 L19.5 19.8 L19.5 23 Z" />
          <rect x="15.2" y="20.6" width="1.6" height="1.6" rx="0.4" />
        </g>
      );
    case 5: // headphones
      return (
        <g stroke={dark} strokeWidth="1" fill={dark}>
          <path d="M5.5 11 Q5.5 2 16 2 Q26.5 2 26.5 11" fill="none" />
          <rect x="4.3" y="9.5" width="3" height="4.5" rx="1.2" />
          <rect x="24.7" y="9.5" width="3" height="4.5" rx="1.2" />
        </g>
      );
    default:
      return null;
  }
}

// ---- face: eyes / brows / mouth, chosen by animation state ----
export function Face({ state }) {
  const dark = "#1a1a1a";
  // Group states into a handful of expressions.
  const happy = state === "excited" || state === "victorious";
  const sad = state === "sad" || state === "defeated";
  const mad = state === "mad";
  const confused = state === "confused";

  const eyeY = 10.6;
  const smooth = { shapeRendering: "geometricPrecision" };

  // eyes
  let eyes;
  if (mad) {
    eyes = (
      <g fill={dark}>
        <rect x="11.4" y="11" width="2.6" height="2" rx="0.4" />
        <rect x="18" y="11" width="2.6" height="2" rx="0.4" />
        {/* angry brows */}
        <path d="M11 9.8 L14.2 11" stroke={dark} strokeWidth="0.9" {...smooth} />
        <path d="M21 9.8 L17.8 11" stroke={dark} strokeWidth="0.9" {...smooth} />
      </g>
    );
  } else if (sad) {
    eyes = (
      <g fill={dark}>
        <rect x="11.4" y={eyeY + 0.6} width="2.4" height="1.6" rx="0.4" />
        <rect x="18.2" y={eyeY + 0.6} width="2.4" height="1.6" rx="0.4" />
        {/* worried brows */}
        <path d="M11 10 L13.8 9.4" stroke={dark} strokeWidth="0.7" {...smooth} />
        <path d="M21 10 L18.2 9.4" stroke={dark} strokeWidth="0.7" {...smooth} />
      </g>
    );
  } else if (happy) {
    // happy closed-arc eyes ^ ^
    eyes = (
      <g fill="none" stroke={dark} strokeWidth="0.9" {...smooth}>
        <path d="M11.2 11.6 Q12.6 10 14 11.6" />
        <path d="M18 11.6 Q19.4 10 20.8 11.6" />
      </g>
    );
  } else {
    // neutral round eyes (also used for thinking / confused)
    eyes = (
      <g fill={dark}>
        <rect x="11.4" y={eyeY} width="2.6" height="2.6" rx="0.6" />
        <rect x="18" y={eyeY} width="2.6" height="2.6" rx="0.6" />
      </g>
    );
  }

  // confused: raise one brow
  const brow = confused ? (
    <path d="M17.8 9.6 L20.8 9" stroke={dark} strokeWidth="0.7" {...smooth} />
  ) : null;

  // mouth
  let mouth;
  if (happy) mouth = <path d="M13 15 Q16 18 19 15" fill="none" stroke={dark} strokeWidth="0.9" {...smooth} />;
  else if (sad) mouth = <path d="M13 16.4 Q16 14 19 16.4" fill="none" stroke={dark} strokeWidth="0.9" {...smooth} />;
  else if (mad) mouth = <path d="M12.6 16 Q16 14.6 19.4 16" fill="none" stroke={dark} strokeWidth="1" {...smooth} />;
  else if (confused) mouth = <ellipse cx="16" cy="15.6" rx="1.2" ry="1.4" fill={dark} {...smooth} />;
  else mouth = <rect x="13.6" y="15.2" width="4.8" height="1" rx="0.5" fill={dark} />;

  // rosy cheeks for the upbeat expressions
  const cheeks = happy ? (
    <g fill="#f2758a" opacity="0.5">
      <circle cx="10.8" cy="13.6" r="1.2" />
      <circle cx="21.2" cy="13.6" r="1.2" />
    </g>
  ) : null;

  return (
    <g>
      {cheeks}
      {eyes}
      {brow}
      {mouth}
    </g>
  );
}
