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
  // Rewards the signed-in player holds: unlocked premium outfit/accessory ids + cheat cards.
  getRewards: (token) => request("/api/profile/rewards", { token }),

  // Public maintenance probe — no auth. { maintenance: bool, message: string? }.
  getStatus: () => request("/api/status"),

  // --- crews (phase 19, signed-in non-guests only; server 403s guests) ---
  getCrews: (token) => request("/api/crews/mine", { token }),
  createCrew: (token, name) => request("/api/crews", { method: "POST", token, body: { name } }),
  joinCrew: (token, code) => request("/api/crews/join", { method: "POST", token, body: { code } }),
  leaveCrew: (token, id) => request(`/api/crews/${id}`, { method: "DELETE", token }),
  disbandCrew: (token, id) => request(`/api/crews/${id}?disband=true`, { method: "DELETE", token }),

  // --- admin dashboard (all gated server-side by the AdminOnly policy) ---
  adminOverview: (token) => request("/api/admin/overview", { token }),
  adminTimeline: (token) => request("/api/admin/timeline", { token }),
  adminFreeTier: (token) => request("/api/admin/freetier", { token }),
  adminUsers: (token, { search = "", page = 1, pageSize = 20 } = {}) => {
    const q = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
    if (search) q.set("search", search);
    return request(`/api/admin/users?${q.toString()}`, { token });
  },
  adminGrant: (token, id, kind) =>
    request(`/api/admin/users/${id}/rewards`, { method: "POST", token, body: { kind } }),
  adminRevoke: (token, id, kind) =>
    request(`/api/admin/users/${id}/rewards?kind=${encodeURIComponent(kind)}`, { method: "DELETE", token }),
  adminMaintenance: (token, on, message) =>
    request("/api/admin/maintenance", { method: "POST", token, body: { on, message } }),
  adminRestart: (token) => request("/api/admin/restart", { method: "POST", token, body: {} }),
  adminWipe: (token, confirm) => request("/api/admin/wipe", { method: "POST", token, body: { confirm } }),
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
