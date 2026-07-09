// Tiny REST helper for the endpoints outside the SignalR hub (samples, stats).
// Returns { ok, status, data } instead of throwing on HTTP errors so screens can
// degrade gracefully — the samples/stats APIs land in later phases and 404 until then.

import { API_BASE } from "./config";

export { API_BASE };

async function request(path, { method = "GET", token, body } = {}) {
  const headers = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined) headers["Content-Type"] = "application/json";

  let res;
  try {
    res = await fetch(`${API_BASE}${path}`, {
      method,
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
  } catch {
    // Network error / API not running — treat like a soft failure the caller can absorb.
    return { ok: false, status: 0, data: null };
  }

  let data = null;
  const text = await res.text().catch(() => "");
  if (text) {
    try { data = JSON.parse(text); } catch { data = text; }
  }
  return { ok: res.ok, status: res.status, data };
}

export const api = {
  getSamples: (token) => request("/api/samples", { token }),
  addSample: (token, text) => request("/api/samples", { method: "POST", token, body: { text } }),
  deleteSample: (token, id) => request(`/api/samples/${id}`, { method: "DELETE", token }),
  getStats: (token) => request("/api/stats/me", { token }),
  // Character: GET returns the saved config or null; PUT saves it (server validates).
  getCharacter: (token) => request("/api/profile/character", { token }),
  putCharacter: (token, config) => request("/api/profile/character", { method: "PUT", token, body: config }),
};

// Estimate client clock skew (ms) vs the server using the HTTP Date response header,
// corrected for round-trip latency. Positive skew => client clock is AHEAD of server.
// Used so countdowns run off the server's UTC deadlines, not a client-started timer.
export async function measureClockSkew() {
  try {
    const t0 = Date.now();
    const res = await fetch(`${API_BASE}/api/health`, { method: "GET", cache: "no-store" });
    const t1 = Date.now();
    const dateHeader = res.headers.get("date");
    if (!dateHeader) return 0;
    const serverMs = new Date(dateHeader).getTime();
    if (Number.isNaN(serverMs)) return 0;
    // Server timestamp corresponds to roughly the midpoint of the round trip.
    const clientMid = t0 + (t1 - t0) / 2;
    return clientMid - serverMs; // client - server
  } catch {
    return 0;
  }
}
