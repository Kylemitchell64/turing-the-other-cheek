import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { useLobby } from "../game/LobbyContext";

export default function HomePage() {
  const { user, logout } = useAuth();
  const { createLobby, joinLobby } = useLobby();
  const navigate = useNavigate();

  const [joining, setJoining] = useState(false);
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState(null);

  const onCreate = async () => {
    setErr(null);
    setBusy(true);
    try {
      await createLobby();
      navigate("/lobby");
    } catch (e) {
      setErr(e.message || "Couldn't create a lobby");
    } finally {
      setBusy(false);
    }
  };

  const onJoin = async (e) => {
    e.preventDefault();
    setErr(null);
    setBusy(true);
    try {
      await joinLobby(code);
      navigate("/lobby");
    } catch (e) {
      setErr(e.message || "Couldn't join that lobby");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="screen">
      <div className="topbar">
        <span className="who">{user?.displayName || user?.unique_name}</span>
        <button className="ghost" onClick={logout}>log out</button>
      </div>

      <div className="panel">
        <h1 className="glow">[ TURING THE OTHER CHEEK ]</h1>
        <p className="tagline">the AI is learning how you type. every game makes it harder to catch.<span className="cursor" /></p>

        {err && <div className="error">{err}</div>}

        {!joining ? (
          <div className="menu">
            <button className="primary" onClick={onCreate} disabled={busy}>
              {busy ? "..." : "create lobby"}
            </button>
            <button className="ghost" onClick={() => { setJoining(true); setErr(null); }} disabled={busy}>
              join by code
            </button>
            <button className="ghost" onClick={() => navigate("/stats")} disabled={busy}>my stats</button>
            <button className="ghost" onClick={() => navigate("/samples")} disabled={busy}>writing samples</button>
          </div>
        ) : (
          <form className="form" onSubmit={onJoin}>
            <input
              type="text"
              placeholder="join code"
              value={code}
              onChange={(e) => setCode(e.target.value.toUpperCase())}
              maxLength={5}
              autoCapitalize="characters"
              autoFocus
            />
            <button type="submit" className="primary" disabled={busy || code.length !== 5}>
              {busy ? "..." : "join"}
            </button>
            <button type="button" className="ghost" onClick={() => { setJoining(false); setCode(""); setErr(null); }}>
              back
            </button>
          </form>
        )}
      </div>
    </div>
  );
}
