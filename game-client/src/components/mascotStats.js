// Phase 34: "most hated by the mascot", counted PER PHONE. No server, no account: the count
// lives in this browser's localStorage, so unlike the session-only battle scars it survives
// a reload, but it never leaves the device and means nothing anywhere else. Every storage
// touch is wrapped: private mode / blocked storage just falls back to an in-memory count
// for this page load, and nothing here can throw into the mascot.
const KEY = "turing.mascotCrashOuts";
export const HARD_HAT_AT = 10; // crash-outs on this phone before it starts wearing a hard hat

let memory = null; // used when storage is unavailable

export function readCrashOuts() {
  try {
    const raw = window.localStorage.getItem(KEY);
    const n = Number.parseInt(raw ?? "", 10);
    if (Number.isFinite(n) && n >= 0) return n;
  } catch { /* blocked storage: fall through */ }
  return memory ?? 0;
}

// +1 and return the new count
export function bumpCrashOuts() {
  const next = readCrashOuts() + 1;
  memory = next;
  try { window.localStorage.setItem(KEY, String(next)); } catch { /* memory only */ }
  return next;
}
