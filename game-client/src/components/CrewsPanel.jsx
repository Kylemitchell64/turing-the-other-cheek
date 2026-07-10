import { useCallback, useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { useLobby } from "../game/LobbyContext";
import { api } from "../api/client";

// Home [CREWS] section (phase 19): persistent groups for signed-in players. Lists the
// crews you belong to (name, members, games, saved config), lets you create one, join by
// code, or tap one to open its live lobby. Guests get a dim teaser instead — crews need a
// durable identity, so the server refuses them anyway.
export default function CrewsPanel() {
  const { token, user } = useAuth();
  const { createCrewLobby } = useLobby();
  const navigate = useNavigate();

  const isGuest = user?.isGuest === "true";

  const [crews, setCrews] = useState([]);
  const [loading, setLoading] = useState(false);
  const [err, setErr] = useState(null);
  const [busy, setBusy] = useState(false);
  const [name, setName] = useState("");
  const [joinCode, setJoinCode] = useState("");
  const [mode, setMode] = useState(null); // null | "create" | "join"

  const load = useCallback(async () => {
    if (isGuest || !token) return;
    setLoading(true);
    const res = await api.getCrews(token);
    if (res.ok && Array.isArray(res.data)) setCrews(res.data);
    setLoading(false);
  }, [token, isGuest]);

  useEffect(() => { load(); }, [load]);

  if (isGuest) {
    return (
      <div className="panel crews">
        <p className="crews-heading">[ CREWS ]</p>
        <p className="crews-teaser">// crews are for signed-in players. log in to keep a permanent group.</p>
      </div>
    );
  }

  const onPlay = async (crew) => {
    setErr(null);
    setBusy(true);
    try {
      await createCrewLobby(crew.id, crew.joinCode);
      navigate("/lobby");
    } catch (e) {
      setErr(e.message || "couldn't open that crew");
      setBusy(false);
    }
  };

  const onCreate = async (e) => {
    e.preventDefault();
    setErr(null);
    setBusy(true);
    const res = await api.createCrew(token, name.trim());
    setBusy(false);
    if (!res.ok) { setErr(res.data?.error || "couldn't create that crew"); return; }
    setName("");
    setMode(null);
    await load();
  };

  const onJoin = async (e) => {
    e.preventDefault();
    setErr(null);
    setBusy(true);
    const res = await api.joinCrew(token, joinCode.trim().toUpperCase());
    setBusy(false);
    if (!res.ok) { setErr(res.data?.error || "couldn't join that crew"); return; }
    setJoinCode("");
    setMode(null);
    await load();
  };

  const onLeave = async (crew) => {
    setErr(null);
    const res = crew.isOwner
      ? await api.disbandCrew(token, crew.id)
      : await api.leaveCrew(token, crew.id);
    if (!res.ok) { setErr(res.data?.error || "couldn't leave that crew"); return; }
    await load();
  };

  return (
    <div className="panel crews">
      <p className="crews-heading">[ CREWS ]</p>
      <p className="crews-sub">// a permanent lobby your group comes back to. the AI keeps learning how you all play.</p>

      {err && <div className="error">{err}</div>}

      {loading ? (
        <p className="soon">// loading…</p>
      ) : crews.length === 0 ? (
        <p className="soon">// no crews yet. make one and share the code.</p>
      ) : (
        <div className="crew-list">
          {crews.map((c) => (
            <div key={c.id} className="crew-row">
              <button className="crew-open" onClick={() => onPlay(c)} disabled={busy} title="open crew lobby">
                <span className="crew-name">{c.name}{c.isOwner ? " ★" : ""}</span>
                <span className="crew-meta">{c.memberCount} member{c.memberCount === 1 ? "" : "s"} · {c.gamesPlayed} game{c.gamesPlayed === 1 ? "" : "s"}</span>
                <span className="crew-chips">
                  <span className="crew-chip">{c.packKey}</span>
                  <span className="crew-chip">{c.difficulty}</span>
                  <span className="crew-chip">{c.paceKey}</span>
                  <span className="crew-chip code">{c.joinCode}</span>
                </span>
              </button>
              <button className="link crew-leave" onClick={() => onLeave(c)} title={c.isOwner ? "disband" : "leave"}>
                {c.isOwner ? "disband" : "leave"}
              </button>
            </div>
          ))}
        </div>
      )}

      {mode === "create" ? (
        <form className="form crew-form" onSubmit={onCreate}>
          <input
            type="text"
            placeholder="crew name (3-24 chars)"
            value={name}
            onChange={(e) => setName(e.target.value)}
            maxLength={24}
            autoFocus
          />
          <button type="submit" className="primary" disabled={busy || name.trim().length < 3}>
            {busy ? "…" : "create"}
          </button>
          <button type="button" className="ghost" onClick={() => { setMode(null); setName(""); setErr(null); }}>cancel</button>
        </form>
      ) : mode === "join" ? (
        <form className="form crew-form" onSubmit={onJoin}>
          <input
            type="text"
            placeholder="crew code"
            value={joinCode}
            onChange={(e) => setJoinCode(e.target.value.toUpperCase())}
            maxLength={5}
            autoCapitalize="characters"
            autoFocus
          />
          <button type="submit" className="primary" disabled={busy || joinCode.trim().length !== 5}>
            {busy ? "…" : "join"}
          </button>
          <button type="button" className="ghost" onClick={() => { setMode(null); setJoinCode(""); setErr(null); }}>cancel</button>
        </form>
      ) : (
        <div className="crew-actions">
          <button className="ghost" onClick={() => { setMode("create"); setErr(null); }}>new crew</button>
          <button className="ghost" onClick={() => { setMode("join"); setErr(null); }}>join by code</button>
        </div>
      )}
    </div>
  );
}
