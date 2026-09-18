import { useLobby } from "../game/LobbyContext";

// Phase 29: while the socket is down (phone lock, tunnel, flaky wifi) say so instead of
// leaving a frozen screen. Auto-reconnect + Rejoin do the actual work in LobbyContext.
export default function ReconnectBanner() {
  const { status } = useLobby();
  if (status !== "reconnecting" && status !== "disconnected") return null;
  return (
    <div className="maint-banner reconnect" role="status">
      <span className="maint-tag">[ {status === "reconnecting" ? "RECONNECTING" : "OFFLINE"} ]</span>
      <span className="maint-msg">
        {status === "reconnecting"
          ? "lost the server for a sec, catching back up…"
          : "no connection. we'll retry when you're back online."}
      </span>
    </div>
  );
}
