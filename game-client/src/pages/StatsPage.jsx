import { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { api } from "../api/client";

export default function StatsPage() {
  const { token, user } = useAuth();
  const navigate = useNavigate();

  const [stats, setStats] = useState(null);
  const [loading, setLoading] = useState(true);
  const [offline, setOffline] = useState(false);

  useEffect(() => {
    let alive = true;
    (async () => {
      const res = await api.getStats(token);
      if (!alive) return;
      if (res.ok && res.data && typeof res.data === "object") {
        setStats(res.data);
        setOffline(false);
      } else {
        setOffline(true);
      }
      setLoading(false);
    })();
    return () => { alive = false; };
  }, [token]);

  const cards = [
    { key: "detectorWins", label: "detector wins", hint: "AIs you caught" },
    { key: "gamesPlayed", label: "games played", hint: "" },
    { key: "timesFooled", label: "times fooled", hint: "wrong accusations" },
    { key: "aiSurvivalGamesWitnessed", label: "ai escapes", hint: "games it survived" },
  ];

  return (
    <div className="screen">
      <div className="topbar">
        <button className="ghost" onClick={() => navigate("/")}>&larr; back</button>
        <span className="who">my stats</span>
      </div>

      <div className="panel">
        <h1 className="glow">{(user?.displayName || user?.unique_name || "you").toUpperCase()}</h1>
        <p className="tagline">your record against the machine.</p>

        {loading ? (
          <p className="soon">// loading…</p>
        ) : offline ? (
          <div className="reveal-box small">
            // stats coming online soon — your record starts tracking with the next update.
          </div>
        ) : (
          <div className="stat-grid">
            {cards.map((c) => (
              <div key={c.key} className="stat-card">
                <div className="stat-num">{stats?.[c.key] ?? 0}</div>
                <div className="stat-lab">{c.label}</div>
                {c.hint && <div className="stat-hint">{c.hint}</div>}
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
