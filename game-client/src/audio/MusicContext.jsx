import { createContext, useContext, useEffect, useMemo, useRef, useState, useCallback } from "react";
import { createChiptune, MOOD_KEYS } from "./chiptune";
import { useLobby } from "../game/LobbyContext";
import { useAuth } from "../auth/AuthContext";

// The mood picker's options: the 5 procedural moods + OFF (local silence).
export const MOOD_OPTIONS = [...MOOD_KEYS, "off"];

const MusicContext = createContext(null);

const LS = {
  mood: "chiptune.mood",
  volume: "chiptune.volume",
  muted: "chiptune.muted",
  follow: "chiptune.followHost",
  enabled: "chiptune.enabled", // has the user ever opted into sound (a gesture happened)
};

const readLS = (k, fallback) => {
  try {
    const v = localStorage.getItem(k);
    return v == null ? fallback : v;
  } catch { return fallback; }
};
const writeLS = (k, v) => { try { localStorage.setItem(k, String(v)); } catch { /* ignore */ } };

// Owns the single chiptune engine + all user music prefs. Drives the engine off three
// inputs: the user's local mood, the room's host-driven mood (when in a lobby and
// "following"), and mute. Persists every choice in localStorage. Browsers block audio
// before a gesture, so nothing sounds until the user opts in (widget "tap for sound" or a
// resume-on-first-click for returning listeners).
export function MusicProvider({ children }) {
  const { lobby, musicMood: hostMood } = useLobby();
  const { user } = useAuth();

  const engineRef = useRef(null);
  if (engineRef.current === null) engineRef.current = createChiptune();
  const supported = engineRef.current.isSupported();

  // Default mood is CHILL and pre-selected ON — menus should hum quietly out of the box.
  // A user who picks OFF (or another mood) has it persisted, so this default only applies to
  // first-timers who've never touched the widget.
  const [localMood, setLocalMood] = useState(() => {
    const m = readLS(LS.mood, "chill");
    return MOOD_OPTIONS.includes(m) ? m : "chill";
  });
  const [volume, setVolumeState] = useState(() => {
    const v = parseFloat(readLS(LS.volume, "0.7"));
    return Number.isFinite(v) ? Math.max(0, Math.min(1, v)) : 0.7;
  });
  const [muted, setMuted] = useState(() => readLS(LS.muted, "false") === "true");
  // In a lobby, default to following the host so one room = one soundtrack. Out of a lobby
  // this is ignored. Persisted so a user who turned it off stays on local control.
  const [followHost, setFollowHost] = useState(() => readLS(LS.follow, "true") === "true");
  // Whether the user has enabled sound this session (a real gesture happened).
  const [started, setStarted] = useState(false);

  const inLobby = !!lobby;
  const amHost = useMemo(() => {
    if (!lobby?.players || !user) return false;
    const me = user.displayName || user.unique_name;
    return lobby.players.some((p) => p.isHost && p.displayName === me);
  }, [lobby, user]);

  // What actually plays: mute wins always; in a lobby while following, the host's mood wins;
  // otherwise the user's local pick. "off" => silence.
  const effectiveMood = useMemo(() => {
    if (muted) return "off";
    if (inLobby && followHost) return hostMood || "off";
    return localMood;
  }, [muted, inLobby, followHost, hostMood, localMood]);

  // Drive the engine from the resolved mood. start() is only reached once the user has
  // opted in, so we never fight the autoplay policy.
  useEffect(() => {
    const engine = engineRef.current;
    if (!engine || !supported) return;
    if (!started || effectiveMood === "off") { engine.stop(); return; }
    engine.setMood(effectiveMood);
    engine.start();
  }, [started, effectiveMood, supported]);

  useEffect(() => {
    if (engineRef.current) engineRef.current.setVolume(volume);
  }, [volume]);

  useEffect(() => () => { if (engineRef.current) engineRef.current.dispose(); }, []);

  // Turn sound on (must be called from a user gesture). Persists the opt-in so returning
  // visitors get music after their next interaction without re-clicking the widget.
  const enable = useCallback(() => {
    setStarted(true);
    writeLS(LS.enabled, "true");
    if (engineRef.current) engineRef.current.start();
  }, []);

  // Music is on by default (phase 27): browsers forbid audio before a gesture, so the closest
  // legal thing to autoplay is to start on the user's FIRST interaction anywhere on the page
  // (a one-shot pointer/key handler). Nothing actually sounds if their persisted mood is OFF
  // or they're muted — effectiveMood handles that — so people who turned it off aren't nagged.
  useEffect(() => {
    if (!supported || started) return;
    const onGesture = () => enable();
    window.addEventListener("pointerdown", onGesture, { once: true });
    window.addEventListener("keydown", onGesture, { once: true });
    return () => {
      window.removeEventListener("pointerdown", onGesture);
      window.removeEventListener("keydown", onGesture);
    };
  }, [supported, started, enable]);

  const setMood = useCallback((m) => {
    if (!MOOD_OPTIONS.includes(m)) return;
    setLocalMood(m);
    writeLS(LS.mood, m);
    if (readLS(LS.enabled, "false") !== "true") { setStarted(true); writeLS(LS.enabled, "true"); }
  }, []);

  const setVolume = useCallback((v) => {
    const clamped = Math.max(0, Math.min(1, v));
    setVolumeState(clamped);
    writeLS(LS.volume, clamped);
  }, []);

  const toggleMute = useCallback(() => {
    setMuted((m) => { const next = !m; writeLS(LS.muted, next); return next; });
  }, []);

  const toggleFollowHost = useCallback(() => {
    setFollowHost((f) => { const next = !f; writeLS(LS.follow, next); return next; });
  }, []);

  const value = {
    supported,
    localMood, setMood,
    volume, setVolume,
    muted, toggleMute,
    followHost, toggleFollowHost,
    started, enable,
    inLobby, amHost, hostMood, effectiveMood,
  };

  return <MusicContext.Provider value={value}>{children}</MusicContext.Provider>;
}

export function useMusic() {
  return useContext(MusicContext);
}
