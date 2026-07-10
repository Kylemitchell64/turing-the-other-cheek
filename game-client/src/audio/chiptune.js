// Procedural 8-bit music engine (phase 21). A tiny Web Audio step-sequencer that loops a
// seeded composition — bass line + lead melody + noise percussion — built from square /
// triangle / noise voices. FIVE moods, each a distinct recipe (scale, tempo, waveform mix,
// density). Everything is DETERMINISTIC per mood: a seeded PRNG picks the notes ONCE when a
// mood is first compiled, so "arcade" sounds the same every visit (never Math.random at
// note-choice time). Background-level master gain, click-free start/stop via short ramps,
// page-visibility pause. Browsers block AudioContext before a user gesture, so nothing is
// created until start() runs off a real interaction.
//
// The engine is framework-agnostic; MusicContext.jsx owns the single instance and React
// wiring. It's intentionally resilient — if Web Audio is missing (old browser, jsdom, a
// locked-down CI chromium) every method no-ops instead of throwing.

// ---- deterministic PRNG ----

// mulberry32 — small, fast, good enough for note choice. Seeded per mood so the melody is
// reproducible run to run.
function mulberry32(seed) {
  let a = seed >>> 0;
  return function () {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// FNV-1a string hash → a stable 32-bit seed. Each mood seeds off its own name so the five
// compositions are distinct but each one is fixed.
function seedFromName(name) {
  let h = 0x811c9dc5;
  for (let i = 0; i < name.length; i++) {
    h ^= name.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return h >>> 0;
}

const midiToFreq = (m) => 440 * Math.pow(2, (m - 69) / 12);

// ---- mood recipes ----
// scale = semitone offsets from the root; root is a MIDI note. Voices are chosen per mood.
// density controls how often the lead/percussion actually fire.
const STEPS = 32; // two bars of 16th notes — enough for a non-repetitive loop
const MOODS = {
  arcade: {
    bpm: 132, root: 60, scale: [0, 2, 4, 7, 9, 12], // major pentatonic — upbeat, happy
    leadWave: "square", bassWave: "square", leadGain: 0.5, bassGain: 0.55,
    density: 0.72, percDensity: 0.7, leadOct: 12, swing: 0.0,
  },
  chill: {
    bpm: 92, root: 57, scale: [0, 2, 3, 5, 7, 9, 10], // A dorian-ish, mellow
    leadWave: "triangle", bassWave: "square", leadGain: 0.42, bassGain: 0.4,
    density: 0.42, percDensity: 0.32, leadOct: 12, swing: 0.18,
  },
  spooky: {
    bpm: 100, root: 55, scale: [0, 1, 3, 5, 7, 8, 11], // harmonic-minor-ish, tense
    leadWave: "square", bassWave: "triangle", leadGain: 0.4, bassGain: 0.45,
    density: 0.34, percDensity: 0.22, leadOct: 12, swing: 0.0,
  },
  hype: {
    bpm: 142, root: 62, scale: [0, 2, 4, 5, 7, 9, 11], // major, fast + driving
    leadWave: "square", bassWave: "square", leadGain: 0.5, bassGain: 0.6,
    density: 0.86, percDensity: 0.9, leadOct: 12, swing: 0.0,
  },
  boss: {
    bpm: 128, root: 48, scale: [0, 2, 3, 5, 7, 8, 10], // low natural minor — dramatic
    leadWave: "square", bassWave: "square", leadGain: 0.46, bassGain: 0.7,
    density: 0.6, percDensity: 0.62, leadOct: 12, swing: 0.0,
  },
};

export const MOOD_KEYS = Object.keys(MOODS); // arcade, chill, spooky, hype, boss

// Compile a mood into fixed per-step note arrays. Cached so a mood only ever compiles once.
const compiled = {};
function compile(mood) {
  if (compiled[mood]) return compiled[mood];
  const r = MOODS[mood];
  const rand = mulberry32(seedFromName(mood));
  const pick = (arr) => arr[Math.floor(rand() * arr.length)];

  const bass = new Array(STEPS).fill(null);
  const lead = new Array(STEPS).fill(null);
  const perc = new Array(STEPS).fill(0); // 0 none, 1 kick, 2 hat

  // Bass: a root/fifth/octave walk that lands on each downbeat (every 4 steps) with the
  // occasional passing note, for a steady foundation.
  const bassNotes = [r.root - 12, r.root - 12 + 7, r.root, r.root - 5];
  for (let i = 0; i < STEPS; i += 4) {
    bass[i] = pick(bassNotes);
    if (rand() < 0.4) bass[i + 2] = pick(bassNotes); // syncopated push
  }

  // Lead: a seeded random-walk melody over the scale, gated by density, biased to step by
  // small intervals so it sounds like a tune rather than noise.
  let deg = Math.floor(rand() * r.scale.length);
  for (let i = 0; i < STEPS; i++) {
    const onBeat = i % 2 === 0;
    const gate = r.density * (onBeat ? 1 : 0.55);
    if (rand() < gate) {
      deg = Math.max(0, Math.min(r.scale.length - 1, deg + (Math.floor(rand() * 3) - 1)));
      const octBump = rand() < 0.18 ? 12 : 0;
      lead[i] = r.root + r.leadOct + r.scale[deg] + octBump;
    }
  }

  // Percussion: kick on the beat, hats filling the off-beats by percDensity.
  for (let i = 0; i < STEPS; i++) {
    if (i % 8 === 0) perc[i] = 1; // kick on the 1 and 3
    else if (rand() < r.percDensity) perc[i] = 2; // hat
  }

  compiled[mood] = { bass, lead, perc, recipe: r };
  return compiled[mood];
}

// ---- the engine ----

export function createChiptune() {
  let ctx = null;
  let master = null; // master gain, ramped for click-free start/stop
  let noiseBuf = null;
  let timer = null; // lookahead scheduler interval
  let mood = "arcade";
  let playing = false;
  let volume = 1; // user 0..1, scaled into the background-level master below
  const BASE_GAIN = 0.15; // background music, never foreground

  let step = 0;
  let nextNoteTime = 0;
  const LOOKAHEAD_MS = 25;
  const SCHEDULE_AHEAD = 0.12; // seconds of audio scheduled in advance

  const supported = () =>
    typeof window !== "undefined" &&
    (window.AudioContext || window.webkitAudioContext);

  function ensureContext() {
    if (ctx || !supported()) return ctx;
    const AC = window.AudioContext || window.webkitAudioContext;
    ctx = new AC();
    master = ctx.createGain();
    master.gain.value = 0; // ramp up on start to avoid a click
    master.connect(ctx.destination);
    // One second of white noise, reused for every percussion hit.
    noiseBuf = ctx.createBuffer(1, ctx.sampleRate, ctx.sampleRate);
    const d = noiseBuf.getChannelData(0);
    for (let i = 0; i < d.length; i++) d[i] = Math.random() * 2 - 1;
    return ctx;
  }

  function targetGain() {
    return BASE_GAIN * Math.max(0, Math.min(1, volume));
  }

  // A single pitched voice with a short pluck envelope.
  function tone(freq, time, dur, type, gain) {
    const osc = ctx.createOscillator();
    const g = ctx.createGain();
    osc.type = type;
    osc.frequency.setValueAtTime(freq, time);
    const peak = gain;
    g.gain.setValueAtTime(0.0001, time);
    g.gain.exponentialRampToValueAtTime(peak, time + 0.008);
    g.gain.exponentialRampToValueAtTime(0.0001, time + dur);
    osc.connect(g).connect(master);
    osc.start(time);
    osc.stop(time + dur + 0.02);
  }

  // A noise burst (percussion) through a filter — kick = low thump, hat = bright tick.
  function noise(time, dur, gain, type) {
    const src = ctx.createBufferSource();
    src.buffer = noiseBuf;
    const filt = ctx.createBiquadFilter();
    if (type === 1) { filt.type = "lowpass"; filt.frequency.value = 220; }
    else { filt.type = "highpass"; filt.frequency.value = 6000; }
    const g = ctx.createGain();
    g.gain.setValueAtTime(gain, time);
    g.gain.exponentialRampToValueAtTime(0.0001, time + dur);
    src.connect(filt).connect(g).connect(master);
    src.start(time);
    src.stop(time + dur + 0.02);
  }

  function scheduleStep(comp, s, time) {
    const r = comp.recipe;
    const b = comp.bass[s];
    if (b != null) tone(midiToFreq(b), time, 0.22, r.bassWave, r.bassGain);
    const l = comp.lead[s];
    if (l != null) tone(midiToFreq(l), time, 0.16, r.leadWave, r.leadGain);
    const p = comp.perc[s];
    if (p === 1) noise(time, 0.14, 0.5, 1);
    else if (p === 2) noise(time, 0.04, 0.14, 2);
  }

  function scheduler() {
    if (!ctx || !playing) return;
    const comp = compile(mood);
    const secondsPerStep = 60 / comp.recipe.bpm / 4; // 16th notes
    while (nextNoteTime < ctx.currentTime + SCHEDULE_AHEAD) {
      // light swing: delay the off-beats a touch on moods that ask for it
      const swing = step % 2 === 1 ? secondsPerStep * comp.recipe.swing : 0;
      scheduleStep(comp, step, nextNoteTime + swing);
      nextNoteTime += secondsPerStep;
      step = (step + 1) % STEPS;
    }
  }

  function startScheduler() {
    if (timer) return;
    step = 0;
    nextNoteTime = ctx.currentTime + 0.06;
    scheduler();
    timer = setInterval(scheduler, LOOKAHEAD_MS);
  }

  function stopScheduler() {
    if (timer) { clearInterval(timer); timer = null; }
  }

  // Public API -------------------------------------------------------------

  // start() MUST run off a user gesture (button/tap) so the AudioContext is allowed to
  // sound. Idempotent; resumes a suspended context (page was backgrounded).
  async function start() {
    if (!supported()) return false;
    ensureContext();
    try { if (ctx.state === "suspended") await ctx.resume(); } catch { /* ignore */ }
    if (playing) return true;
    playing = true;
    const now = ctx.currentTime;
    master.gain.cancelScheduledValues(now);
    master.gain.setValueAtTime(Math.max(0.0001, master.gain.value), now);
    master.gain.linearRampToValueAtTime(targetGain(), now + 0.08); // fade in, no click
    startScheduler();
    if (typeof window !== "undefined") window.__chiptuneStarted = true;
    return true;
  }

  // stop() ramps out then tears down the scheduler. The context is kept (cheap) so a later
  // start() is instant.
  function stop() {
    if (!ctx || !playing) { playing = false; stopScheduler(); return; }
    playing = false;
    const now = ctx.currentTime;
    master.gain.cancelScheduledValues(now);
    master.gain.setValueAtTime(master.gain.value, now);
    master.gain.linearRampToValueAtTime(0.0001, now + 0.1); // fade out, no click
    // Let the tail finish, then kill the scheduler.
    setTimeout(stopScheduler, 140);
  }

  function setMood(next) {
    if (!MOODS[next]) return;
    if (next === mood) return;
    mood = next;
    // Restart the loop from the top of the new composition for a clean phrase change.
    if (playing && ctx) { step = 0; nextNoteTime = ctx.currentTime + 0.04; }
  }

  function setVolume(v) {
    volume = Math.max(0, Math.min(1, v));
    if (ctx && master && playing) {
      const now = ctx.currentTime;
      master.gain.cancelScheduledValues(now);
      master.gain.linearRampToValueAtTime(Math.max(0.0001, targetGain()), now + 0.05);
    }
  }

  // Page-visibility: suspend audio when the tab is hidden, resume when it's back (only if
  // we were playing). Saves CPU/battery and stops music leaking from a backgrounded tab.
  function handleVisibility() {
    if (!ctx) return;
    if (document.hidden) { try { ctx.suspend(); } catch { /* ignore */ } }
    else if (playing) { try { ctx.resume(); } catch { /* ignore */ } }
  }
  if (typeof document !== "undefined") {
    document.addEventListener("visibilitychange", handleVisibility);
  }

  function dispose() {
    stop();
    stopScheduler();
    if (typeof document !== "undefined")
      document.removeEventListener("visibilitychange", handleVisibility);
    try { ctx && ctx.close(); } catch { /* ignore */ }
    ctx = null;
  }

  return {
    start, stop, setMood, setVolume, dispose,
    isPlaying: () => playing,
    getMood: () => mood,
    isSupported: () => !!supported(),
  };
}
