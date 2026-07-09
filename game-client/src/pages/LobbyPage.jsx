import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { useLobby } from "../game/LobbyContext";
import { PACKS, packFor } from "../game/packs";

export default function LobbyPage() {
  const { user } = useAuth();
  const { lobby, roster, packKey, setLobbyOptions, startGame, leaveLobby } = useLobby();
  const navigate = useNavigate();

  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState(null);
  const [copied, setCopied] = useState(false);

  // No lobby in state (e.g. hard refresh lost the socket) → back to home.
  useEffect(() => {
    if (!lobby && !roster) navigate("/", { replace: true });
  }, [lobby, roster, navigate]);

  // When the host starts, every client gets GameStarted → head to the game screen.
  useEffect(() => {
    if (roster) navigate("/game", { replace: true });
  }, [roster, navigate]);

  if (!lobby) return null;

  const myName = user?.displayName || user?.unique_name;
  const me = lobby.players.find((p) => p.displayName === myName);
  const amHost = me?.isHost;
  const canStart = lobby.players.length >= 3 && lobby.players.length <= 8;

  const activePack = packFor(packKey);

  const onPickPack = async (key) => {
    if (key === packKey) return;
    try {
      await setLobbyOptions(key);
    } catch (e) {
      setErr(e.message || "Couldn't change the pack");
    }
  };

  const copyCode = async () => {
    try {
      await navigator.clipboard.writeText(lobby.code);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch { /* clipboard blocked — the code is on screen anyway */ }
  };

  const onStart = async () => {
    setErr(null);
    setBusy(true);
    try {
      await startGame();
    } catch (e) {
      setErr(e.message || "Couldn't start");
      setBusy(false);
    }
  };

  const onLeave = async () => {
    await leaveLobby();
    navigate("/", { replace: true });
  };

  return (
    <div className="screen">
      <div className="topbar">
        <span className="who">{myName}</span>
        <button className="ghost" onClick={onLeave}>leave</button>
      </div>

      <div className="panel">
        <h1 className="glow">[ LOBBY ]</h1>
        <p className="tagline">share the code. 3–8 players.<span className="cursor" /></p>

        <button className="code" onClick={copyCode} title="tap to copy">
          {lobby.code.split("").map((c, i) => (
            <span key={i} className="code-char">{c}</span>
          ))}
        </button>
        <p className="soon">{copied ? "copied!" : "// tap the code to copy"}</p>

        <div className="roster">
          {lobby.players.map((p) => (
            <div key={p.displayName} className={p.isConnected ? "seat" : "seat off"}>
              <span className="dot" />
              <span className="seat-name">
                {p.displayName}{p.displayName === myName ? " (you)" : ""}
              </span>
              {p.isHost && <span className="badge">host</span>}
            </div>
          ))}
        </div>
        <p className="soon">// everyone starts with 3 fake-out tokens</p>

        <div className="pack-picker">
          <p className="pack-heading">// prompt pack</p>
          <div className="segmented">
            {PACKS.map((p) => {
              const selected = p.key === packKey;
              const cls = ["seg", selected ? "on" : "", amHost ? "" : "locked"].join(" ").trim();
              return (
                <button
                  key={p.key}
                  className={cls}
                  onClick={() => amHost && onPickPack(p.key)}
                  disabled={!amHost}
                  aria-pressed={selected}
                >
                  {p.label}
                </button>
              );
            })}
          </div>
          <p className="pack-desc">{activePack.description}</p>
          {activePack.ageNote && <p className="pack-age">{activePack.ageNote}</p>}
          {!amHost && <p className="soon">// the host picks the pack</p>}
        </div>

        {err && <div className="error">{err}</div>}

        {amHost ? (
          <button className="primary" onClick={onStart} disabled={busy || !canStart}>
            {busy ? "..." : canStart ? "start game" : `need ${Math.max(0, 3 - lobby.players.length)} more`}
          </button>
        ) : (
          <p className="soon">// waiting for the host to start</p>
        )}
      </div>
    </div>
  );
}
