import { useState, useEffect, useCallback } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { api } from "../api/client";

const MAX_CHARS = 10000;

// "How well the AI knows you" grows with how many samples you've fed it. Pure UI
// flavor mapped off the sample count — the pitch is that every sample sharpens it.
function knowledge(count) {
  if (count <= 0) return { level: 0, label: "no data — you're a stranger", pct: 4 };
  if (count === 1) return { level: 1, label: "faint signal", pct: 22 };
  if (count === 2) return { level: 2, label: "getting a read on you", pct: 42 };
  if (count <= 4) return { level: 3, label: "learning your voice", pct: 64 };
  if (count <= 7) return { level: 4, label: "knows how you type", pct: 84 };
  return { level: 5, label: "wears you like a mask", pct: 100 };
}

export default function WritingSamplesPage() {
  const { token } = useAuth();
  const navigate = useNavigate();

  const [samples, setSamples] = useState([]);
  const [text, setText] = useState("");
  const [loading, setLoading] = useState(true);
  const [offline, setOffline] = useState(false); // API not up yet (404 / network)
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState(null);

  const load = useCallback(async () => {
    setLoading(true);
    const res = await api.getSamples(token);
    if (res.ok && Array.isArray(res.data)) {
      setSamples(res.data);
      setOffline(false);
    } else {
      // 404 (endpoint ships in a later phase) or network error → degrade gracefully.
      setOffline(true);
    }
    setLoading(false);
  }, [token]);

  useEffect(() => { load(); }, [load]);

  const onAdd = async (e) => {
    e.preventDefault();
    const t = text.trim();
    if (!t) return;
    setErr(null);
    setBusy(true);
    const res = await api.addSample(token, t.slice(0, MAX_CHARS));
    setBusy(false);
    if (res.ok) {
      setText("");
      load();
    } else if (res.status === 404 || res.status === 0) {
      setOffline(true);
    } else {
      setErr("couldn't save that sample");
    }
  };

  const onDelete = async (id) => {
    const res = await api.deleteSample(token, id);
    if (res.ok) load();
    else if (res.status === 404 || res.status === 0) setOffline(true);
    else setErr("couldn't delete that sample");
  };

  const k = knowledge(samples.length);
  const over = text.length > MAX_CHARS;

  return (
    <div className="screen">
      <div className="topbar">
        <button className="ghost" onClick={() => navigate("/")}>&larr; back</button>
        <span className="who">writing samples</span>
      </div>

      <div className="panel">
        <h1 className="glow">TEACH THE MACHINE</h1>
        <p className="tagline">
          paste how you actually write. the AI studies it to blend in as you. every
          sample makes it harder to catch.
        </p>

        {/* AI knowledge indicator */}
        <div className="knowledge">
          <div className="knowledge-head">
            <span className="knowledge-label">// AI knowledge of you</span>
            <span className="knowledge-lv">lv {k.level}</span>
          </div>
          <div className="meter">
            <div className={`meter-fill lv${k.level}`} style={{ width: `${k.pct}%` }} />
          </div>
          <div className="knowledge-sub">{k.label}</div>
        </div>

        {offline ? (
          <div className="reveal-box small">
            // sample vault coming online soon — this feature ships with the next update.
          </div>
        ) : (
          <>
            <form className="form" onSubmit={onAdd}>
              <textarea
                className="sample-input"
                placeholder="paste a chunk of your writing — texts, emails, notes, anything in your voice…"
                value={text}
                onChange={(e) => setText(e.target.value.slice(0, MAX_CHARS))}
                maxLength={MAX_CHARS}
                rows={6}
              />
              <div className="counter-row">
                <span className={over ? "counter over" : "counter"}>
                  {text.length.toLocaleString()} / {MAX_CHARS.toLocaleString()}
                </span>
                <button className="primary" type="submit" disabled={busy || !text.trim()}>
                  {busy ? "saving…" : "add sample"}
                </button>
              </div>
            </form>

            {err && <div className="error">{err}</div>}

            <h3 className="section">your samples ({samples.length})</h3>
            {loading ? (
              <p className="soon">// loading…</p>
            ) : samples.length === 0 ? (
              <p className="soon">// none yet. paste one above.</p>
            ) : (
              <div className="sample-list">
                {samples.map((s) => (
                  <div key={s.id} className="sample-item">
                    <p className="sample-snippet">
                      {(s.text || "").slice(0, 160)}
                      {(s.text || "").length > 160 ? "…" : ""}
                    </p>
                    <div className="sample-foot">
                      <span className="sample-meta">
                        {s.source === "Game" ? "from a game" : "pasted"} · {(s.text || "").length} chars
                      </span>
                      <button className="mini-del" onClick={() => onDelete(s.id)}>delete</button>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
