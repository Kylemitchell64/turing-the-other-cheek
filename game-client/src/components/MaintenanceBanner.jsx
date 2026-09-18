import { useEffect, useState } from "react";
import { api } from "../api/client";

// Polls the public /api/status probe and shows the operator's maintenance message when the
// game is paused. Read-only and unauthenticated, so it works on Home AND the login screen.
export default function MaintenanceBanner() {
  const [status, setStatus] = useState({ maintenance: false, message: null, storage: "postgres" });
  // The API lives on a free tier that spins down when idle (deliberately: keeping it awake
  // 24/7 burns the whole month's free hours). The first request after a nap takes 30-60s.
  // If the status probe hasn't answered in a few seconds, say so instead of looking frozen.
  const [waking, setWaking] = useState(false);

  useEffect(() => {
    let alive = true;
    let answered = false;
    const poll = async () => {
      const slow = setTimeout(() => { if (alive && !answered) setWaking(true); }, 2500);
      const { ok, data } = await api.getStatus();
      clearTimeout(slow);
      if (!alive) return;
      if (ok && data) {
        answered = true;
        setWaking(false);
        setStatus(data);
      }
    };
    poll();
    const id = setInterval(poll, 20000);
    return () => { alive = false; clearInterval(id); };
  }, []);

  if (waking) {
    return (
      <div className="maint-banner" role="status">
        <span className="maint-tag">[ WAKING UP ]</span>
        <span className="maint-msg">
          the server naps between games to stay free, first load can take up to a minute
        </span>
      </div>
    );
  }

  if (status.storage === "memory" && !status.maintenance) {
    return (
      <div className="maint-banner" role="status">
        <span className="maint-tag">[ TEMP MODE ]</span>
        <span className="maint-msg">
          the database is napping, so you can play but stats and style profiles won&apos;t save this session
        </span>
      </div>
    );
  }

  if (!status.maintenance) return null;

  return (
    <div className="maint-banner" role="status">
      <span className="maint-tag">[ MAINTENANCE ]</span>
      <span className="maint-msg">
        {status.message || "the game is paused for maintenance, check back soon"}
      </span>
    </div>
  );
}
