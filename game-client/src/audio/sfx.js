// Tiny synthesized sound effects (phase 29). Independent of the chiptune sequencer so a
// player with the music OFF still hears the game react; shares its volume/mute via
// MusicContext. Everything is Web Audio primitives — no samples, no network — and every
// effect is under half a second. Nothing plays until unlock() has run inside a real user
// gesture (autoplay policy); before that, calls are silently dropped.
//
// A tiny event bus lets things outside the React tree (the mascot's physics loop) ask for
// a sound without threading context through: sfxBus.emit("land").

const listeners = new Set();
export const sfxBus = {
  emit(name) { listeners.forEach((fn) => { try { fn(name); } catch { /* ignore */ } }); },
  on(fn) { listeners.add(fn); return () => listeners.delete(fn); },
};

export function createSfx() {
  let ctx = null;
  let out = null;
  let noiseBuf = null;
  let volume = 0.7;
  let muted = false;
  const BASE = 0.32; // louder than the background music, still not a jump-scare

  const supported = () =>
    typeof window !== "undefined" && (window.AudioContext || window.webkitAudioContext);

  const ensure = () => {
    if (ctx || !supported()) return ctx;
    const AC = window.AudioContext || window.webkitAudioContext;
    ctx = new AC();
    out = ctx.createGain();
    out.gain.value = muted ? 0 : BASE * volume;
    out.connect(ctx.destination);
    noiseBuf = ctx.createBuffer(1, ctx.sampleRate / 2, ctx.sampleRate);
    const d = noiseBuf.getChannelData(0);
    for (let i = 0; i < d.length; i++) d[i] = Math.random() * 2 - 1;
    return ctx;
  };

  // Call from a user gesture. Creates the context and resumes it if the browser parked it.
  const unlock = () => {
    const c = ensure();
    if (c && c.state === "suspended") c.resume().catch(() => {});
  };

  const applyGain = () => {
    if (!out) return;
    out.gain.setTargetAtTime(muted ? 0 : BASE * volume, ctx.currentTime, 0.01);
  };
  const setVolume = (v) => { volume = Math.max(0, Math.min(1, v)); applyGain(); };
  const setMuted = (m) => { muted = !!m; applyGain(); };

  // ---- primitives ----
  const tone = (type, f0, f1, t0, dur, peak = 1, curve = "exp") => {
    const o = ctx.createOscillator();
    const g = ctx.createGain();
    o.type = type;
    o.frequency.setValueAtTime(f0, t0);
    if (f1 && f1 !== f0) {
      if (curve === "exp") o.frequency.exponentialRampToValueAtTime(Math.max(1, f1), t0 + dur);
      else o.frequency.linearRampToValueAtTime(f1, t0 + dur);
    }
    g.gain.setValueAtTime(0.0001, t0);
    g.gain.exponentialRampToValueAtTime(peak, t0 + 0.008);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
    o.connect(g).connect(out);
    o.start(t0);
    o.stop(t0 + dur + 0.02);
  };
  const noise = (t0, dur, peak = 0.6, hp = 800) => {
    const s = ctx.createBufferSource();
    s.buffer = noiseBuf;
    const f = ctx.createBiquadFilter();
    f.type = "highpass";
    f.frequency.value = hp;
    const g = ctx.createGain();
    g.gain.setValueAtTime(peak, t0);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
    s.connect(f).connect(g).connect(out);
    s.start(t0);
    s.stop(t0 + dur + 0.02);
  };

  // ---- the effects ----
  const FX = {
    // someone pointed a finger: two rising square blips
    accuse(t) { tone("square", 330, 330, t, 0.07, 0.5); tone("square", 494, 660, t + 0.09, 0.14, 0.5); },
    // FAKE-OUT: a noise slam + a low thump, matches the screen shake
    veto(t) { noise(t, 0.22, 0.7, 300); tone("sine", 110, 40, t, 0.28, 0.9); tone("square", 220, 90, t + 0.02, 0.18, 0.3); },
    // countdown's last seconds
    tick(t) { tone("square", 1320, 1320, t, 0.035, 0.28); },
    // last-second tick, a touch lower and longer
    tickLow(t) { tone("square", 880, 880, t, 0.06, 0.32); },
    // the AI got caught / you won: quick major arpeggio
    win(t) {
      [523, 659, 784, 1047].forEach((f, i) => tone("square", f, f, t + i * 0.085, 0.16, 0.42));
      tone("triangle", 1047, 1047, t + 0.34, 0.35, 0.3);
    },
    // the AI got away: three sagging notes
    lose(t) { [392, 349, 294].forEach((f, i) => tone("sawtooth", f, f * 0.94, t + i * 0.17, 0.2, 0.32)); },
    // answers land on screen
    reveal(t) { tone("triangle", 660, 990, t, 0.09, 0.3); },
    // new prompt
    prompt(t) { tone("square", 440, 440, t, 0.05, 0.3); tone("square", 554, 554, t + 0.06, 0.07, 0.3); },
    // mascot: pokes get higher-pitched the madder it is (pitch passed via variant)
    poke(t, v = 0) { tone("square", 300 + v * 40, 360 + v * 50, t, 0.05, 0.35); },
    grab(t) { tone("triangle", 520, 720, t, 0.08, 0.3); },
    land(t) { tone("sine", 120, 45, t, 0.12, 0.8); noise(t, 0.06, 0.25, 2000); },
    // crash-out: glitch static then a falling whine
    crash(t) {
      for (let i = 0; i < 6; i++) noise(t + i * 0.05, 0.035, 0.5, 400 + i * 600);
      tone("sawtooth", 900, 60, t + 0.3, 0.45, 0.4);
    },
    // faceplant
    thud(t) { tone("sine", 90, 30, t, 0.22, 1.0); noise(t, 0.12, 0.4, 500); },
    // dusting off: two tiny brushes
    dust(t) { noise(t, 0.05, 0.18, 3000); noise(t + 0.14, 0.05, 0.18, 3000); },
  };

  const play = (name, variant = 0) => {
    if (!supported()) return;
    const c = ensure();
    if (!c || c.state !== "running") return; // no gesture yet — stay silent
    const fx = FX[name];
    if (!fx) return;
    try { fx(c.currentTime + 0.005, variant); } catch { /* audio hiccup — cosmetic */ }
  };

  return { play, unlock, setVolume, setMuted, isSupported: () => !!supported() };
}
