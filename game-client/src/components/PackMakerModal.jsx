import { useState } from "react";
import { api } from "../api/client";
import { useAuth } from "../auth/AuthContext";

// The AI category maker (phase 20). Host-only. Two ways in:
//   1. type a theme -> AI generates a pack (behind server-side safety guardrails)
//   2. paste a share-code -> decode a pack someone made earlier ("paste here to resume")
// Either lands on a preview (name, nsfw tag, scrollable prompts) with USE / COPY CODE /
// email-it-to-yourself. USE installs it into the live lobby via setCustomPack(code).
export default function PackMakerModal({ onUse, onClose }) {
  const { token, user } = useAuth();

  const [theme, setTheme] = useState("");
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState(null);
  const [preview, setPreview] = useState(null); // { name, nsfw, prompts[], code }
  const [copied, setCopied] = useState(false);
  const [using, setUsing] = useState(false);

  const onGenerate = async () => {
    const t = theme.trim();
    if (t.length < 3 || t.length > 80) {
      setErr("give it a theme, 3-80 characters");
      return;
    }
    setErr(null);
    setBusy(true);
    const res = await api.generatePack(token, t, 20);
    setBusy(false);
    if (res.ok && res.data) {
      setPreview(res.data);
    } else if (res.status === 429) {
      setErr(res.data?.error || "slow down a sec");
    } else if (res.status === 422) {
      setErr(res.data?.error || "couldn't make that one, try a different theme");
    } else {
      setErr(res.data?.error || "something went wrong, try again");
    }
  };

  const onDecode = async () => {
    const c = code.trim();
    if (!c) {
      setErr("paste a code first");
      return;
    }
    setErr(null);
    setBusy(true);
    const res = await api.decodePack(token, c);
    setBusy(false);
    if (res.ok && res.data) {
      setPreview(res.data);
    } else {
      setErr(res.data?.error || "that code didn't work");
    }
  };

  const onCopy = async () => {
    try {
      await navigator.clipboard.writeText(preview.code);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch { /* clipboard blocked — the code is shown below anyway */ }
  };

  const onUseClick = async () => {
    setErr(null);
    setUsing(true);
    try {
      await onUse(preview.code);
      onClose();
    } catch (e) {
      setUsing(false);
      setErr(e?.message || "couldn't use that pack");
    }
  };

  // mailto so the host can send the code to themselves — no email service (locked decision).
  const mailtoHref = preview
    ? `mailto:${encodeURIComponent(user?.email || "")}` +
      `?subject=${encodeURIComponent(`my party pack: ${preview.name}`)}` +
      `&body=${encodeURIComponent(
        `paste this code into a lobby to resume your custom pack "${preview.name}":\n\n${preview.code}`
      )}`
    : "#";

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal pack-maker" onClick={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <h2 className="glow">[ MAKE YOUR OWN ]</h2>
          <button className="ghost" onClick={onClose}>close</button>
        </div>

        {!preview ? (
          <>
            <p className="pack-heading">// describe your category</p>
            <input
              className="text-input"
              value={theme}
              onChange={(e) => setTheme(e.target.value)}
              placeholder="e.g. 90s cartoons, camping disasters, office gossip"
              maxLength={80}
              disabled={busy}
            />
            <button className="primary" onClick={onGenerate} disabled={busy}>
              {busy ? "generating..." : "generate with ai"}
            </button>
            {busy && (
              <p className="soon terminal-spin">// asking the ai<span className="cursor" /></p>
            )}
            <p className="pack-age">
              // the ai makes 20 prompts on your theme. it won't make anything hateful,
              sexual-about-minors, or dangerous — those themes just bounce.
            </p>

            <div className="pack-divider">— or paste a code to resume —</div>
            <input
              className="text-input"
              value={code}
              onChange={(e) => setCode(e.target.value)}
              placeholder="TTOC1...."
              disabled={busy}
            />
            <button className="ghost wide" onClick={onDecode} disabled={busy}>
              paste a code
            </button>

            {err && <div className="error">{err}</div>}
          </>
        ) : (
          <>
            <p className="pack-heading">
              // {preview.name}
              {preview.nsfw && <span className="badge nsfw">18+</span>}
            </p>
            {preview.nsfw && (
              <p className="pack-age">18+ // adult humor. for a room that's all adults.</p>
            )}
            <div className="pack-preview">
              {preview.prompts.map((p, i) => (
                <div key={i} className="pack-preview-line">{p}</div>
              ))}
            </div>

            {err && <div className="error">{err}</div>}

            <div className="pack-actions">
              <button className="primary" onClick={onUseClick} disabled={using}>
                {using ? "..." : "use in this lobby"}
              </button>
              <button className="ghost" onClick={onCopy}>
                {copied ? "copied!" : "copy code"}
              </button>
              <a className="ghost as-link" href={mailtoHref}>email it to yourself</a>
            </div>
            <button className="ghost wide" onClick={() => { setPreview(null); setErr(null); }}>
              back
            </button>
          </>
        )}
      </div>
    </div>
  );
}
