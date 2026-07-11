import { useState } from "react";
import { useMusic, MOOD_OPTIONS } from "../audio/MusicContext";
import { useLobby } from "../game/LobbyContext";

// Compact terminal-styled music widget, pinned to a bottom corner and collapsible. Mounted
// at App level so it's on every screen. Mood picker (5 + OFF), volume slider, mute, and — in
// a lobby — a "follow host" toggle so one room plays one shared soundtrack. Browsers block
// audio until a gesture, so an un-started widget shows a "tap for sound" affordance.
const MOOD_LABEL = {
  arcade: "ARCADE", chill: "CHILL", spooky: "SPOOKY",
  hype: "HYPE", boss: "BOSS", off: "OFF",
};

// Inline music-note glyph. The old text "♪" rendered as tofu on phones whose Courier
// fallback lacks the glyph, so it's an SVG now — currentColor keeps the neon fill + hover
// glow working. `slashed` draws the "no sound" bar for muted/off.
function MusicNote({ slashed = false }) {
  return (
    <svg
      className="music-note-svg"
      viewBox="0 0 24 24"
      width="1em"
      height="1em"
      aria-hidden="true"
      focusable="false"
    >
      <path
        d="M9 17V6l10-2v9"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
      <circle cx="6.5" cy="17" r="2.6" fill="currentColor" />
      <circle cx="16.5" cy="15" r="2.6" fill="currentColor" />
      {slashed && (
        <line
          x1="3" y1="21" x2="21" y2="3"
          stroke="currentColor" strokeWidth="2" strokeLinecap="round"
        />
      )}
    </svg>
  );
}

export default function MusicWidget() {
  const {
    supported, localMood, setMood, volume, setVolume,
    muted, toggleMute, followHost, toggleFollowHost,
    started, enable, inLobby, amHost, hostMood,
  } = useMusic();
  const { setLobbyMusic } = useLobby();
  const [open, setOpen] = useState(false);

  if (!supported) return null; // no Web Audio (old browser / jsdom) — render nothing, never throw

  // When following the host in a lobby, the shown/selected mood is the room's; otherwise it's
  // the user's local pick. The host drives the room via setLobbyMusic; everyone else is read-only.
  const following = inLobby && followHost;
  const shownMood = following ? (hostMood || "off") : localMood;
  const canPick = !following || amHost;

  const pickMood = (m) => {
    if (!started) enable();
    if (following && amHost) setLobbyMusic(m);
    else if (!following) setMood(m);
    // (following && !amHost) => read-only, no-op
  };

  return (
    <div className={`music-widget${open ? " open" : ""}`}>
      {!open ? (
        <button
          type="button"
          className="music-fab"
          onClick={() => setOpen(true)}
          aria-label="open music controls"
          title="music"
        >
          <span className="music-note"><MusicNote slashed={muted || shownMood === "off"} /></span>
          {!started && <span className="music-hint">tap for sound</span>}
        </button>
      ) : (
        <div className="music-panel">
          <div className="music-head">
            <span className="music-title"><MusicNote /> MUSIC</span>
            <button type="button" className="music-x" onClick={() => setOpen(false)} aria-label="collapse">
              ▾
            </button>
          </div>

          {!started && (
            <p className="music-tip">pick a mood to start the chiptune</p>
          )}

          <div className="music-moods" role="group" aria-label="music mood">
            {MOOD_OPTIONS.map((m) => (
              <button
                key={m}
                type="button"
                className={`music-mood${shownMood === m ? " sel" : ""}`}
                onClick={() => pickMood(m)}
                disabled={!canPick}
                aria-pressed={shownMood === m}
              >
                {MOOD_LABEL[m]}
              </button>
            ))}
          </div>

          <label className="music-vol">
            VOL
            <input
              type="range"
              min="0"
              max="1"
              step="0.05"
              value={volume}
              onChange={(e) => setVolume(parseFloat(e.target.value))}
              aria-label="music volume"
            />
          </label>

          <div className="music-row">
            <button
              type="button"
              className={`music-toggle${muted ? " on" : ""}`}
              onClick={toggleMute}
              aria-pressed={muted}
            >
              {muted ? "MUTED" : "MUTE"}
            </button>

            {inLobby && (
              <button
                type="button"
                className={`music-toggle${followHost ? " on" : ""}`}
                onClick={toggleFollowHost}
                aria-pressed={followHost}
                title="play the host's room soundtrack"
              >
                {followHost ? "FOLLOW ✓" : "FOLLOW"}
              </button>
            )}
          </div>

          {following && !amHost && (
            <p className="music-note-line">// following host: {MOOD_LABEL[shownMood]}</p>
          )}
          {following && amHost && (
            <p className="music-note-line">// you set the room's music</p>
          )}
        </div>
      )}
    </div>
  );
}
