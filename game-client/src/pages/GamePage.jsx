import { useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { useLobby } from "../game/LobbyContext";

// Placeholder for phase 3. For now it just shows the shuffled roster the server
// sent in GameStarted — humans + the AI, indistinguishable.
export default function GamePage() {
  const { roster, leaveLobby } = useLobby();
  const navigate = useNavigate();

  useEffect(() => {
    if (!roster) navigate("/", { replace: true });
  }, [roster, navigate]);

  if (!roster) return null;

  const onLeave = async () => {
    await leaveLobby();
    navigate("/", { replace: true });
  };

  return (
    <div className="screen">
      <div className="topbar">
        <span className="who">game on</span>
        <button className="ghost" onClick={onLeave}>leave</button>
      </div>

      <div className="panel">
        <h1 className="glow">THE GAME</h1>
        <p className="tagline">one of them isn't human. rounds start soon.</p>

        <div className="roster">
          {roster.map((p, i) => (
            <div key={i} className="seat">
              <span className="dot" />
              <span className="seat-name">{p.displayName}</span>
              <span className="badge">{p.tokensRemaining} tokens</span>
            </div>
          ))}
        </div>

        <p className="soon">// round loop, prompts + accusations coming in the next build</p>
      </div>
    </div>
  );
}
