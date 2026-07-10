import { useEffect, useState } from "react";
import { api } from "../api/client";

// Polls the public /api/status probe and shows the operator's maintenance message when the
// game is paused. Read-only and unauthenticated, so it works on Home AND the login screen.
export default function MaintenanceBanner() {
  const [status, setStatus] = useState({ maintenance: false, message: null });

  useEffect(() => {
    let alive = true;
    const poll = async () => {
      const { ok, data } = await api.getStatus();
      if (alive && ok && data) setStatus(data);
    };
    poll();
    const id = setInterval(poll, 20000);
    return () => { alive = false; clearInterval(id); };
  }, []);

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
