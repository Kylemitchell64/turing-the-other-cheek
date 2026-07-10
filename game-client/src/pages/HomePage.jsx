import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { useLobby } from "../game/LobbyContext";
import MaintenanceBanner from "../components/MaintenanceBanner";

export default function HomePage() {
  const { user, logout } = useAuth();
  const { createLobby, joinLobby } = useLobby();
  const navigate = useNavigate();

  const [joining, setJoining] = useState(false);
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState(null);
  const [showAbout, setShowAbout] = useState(false);

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

  const isAdmin = user?.isAdmin === "true";

  return (
    <div className="screen">
      <div className="topbar">
        <span className="who">{user?.displayName || user?.unique_name}</span>
        <div className="topbar-actions">
          {isAdmin && (
            <button className="link admin-link" onClick={() => navigate("/admin")}>[ADMIN]</button>
          )}
          <button className="ghost" onClick={logout}>log out</button>
        </div>
      </div>

      <MaintenanceBanner />

      <div className="home-grid">
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
            <button className="ghost" onClick={() => navigate("/character", { state: { edit: true } })} disabled={busy}>edit character</button>
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

      <div className="panel about">
        <button
          type="button"
          className="about-toggle"
          onClick={() => setShowAbout((s) => !s)}
          aria-expanded={showAbout}
        >
          [ WHAT IS THIS? ] <span className="about-caret">{showAbout ? "−" : "+"}</span>
        </button>
        {showAbout && (
          <div className="about-body">
            <p>
              back in 1950 Alan Turing asked a simple question: could a machine hold a
              text conversation well enough that you couldn't tell it apart from a person?
              he called it the imitation game. no robots, no sci-fi, just words on a
              screen and one job: figure out who's real.
            </p>
            <p>
              that's this game, except the machine is sitting in your group chat. every
              lobby has one AI player wearing a normal name, answering the same dumb
              prompts you are. and it's been reading how you and your friends actually
              type, so it blends. your job is to catch it before it outlasts everyone.
            </p>
            <p>
              "turning the other cheek" is supposed to be about letting things slide.
              we are doing the exact opposite. nobody here is forgiving anybody. we're
              hunting the imposter, and if you guess wrong it costs you.
            </p>
            <p className="about-foot">// pick a name, grab some friends, find the machine.</p>
          </div>
        )}
      </div>
      </div>
    </div>
  );
}
