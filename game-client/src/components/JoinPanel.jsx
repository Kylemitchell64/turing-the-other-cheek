import { useEffect, useRef, useState } from "react";

// Join a lobby (phase 31): type the 5-character code, or scan the host's QR with the phone
// camera. Scanning uses the browser's BarcodeDetector where it exists (Android Chrome);
// elsewhere we say so and point at the camera app, which opens the join link anyway.

const CODE_RE = /^[A-Z2-9]{5}$/;

function codeFromScan(raw) {
  if (!raw) return null;
  const s = String(raw).trim();
  try {
    const u = new URL(s);
    const j = (u.searchParams.get("join") || "").toUpperCase();
    if (CODE_RE.test(j)) return j;
  } catch { /* not a url */ }
  const up = s.toUpperCase();
  return CODE_RE.test(up) ? up : null;
}

export default function JoinPanel({ onJoin, onBack, busy, error }) {
  const [code, setCode] = useState("");
  const [scanning, setScanning] = useState(false);
  const canScan = typeof window !== "undefined" && "BarcodeDetector" in window && !!navigator.mediaDevices?.getUserMedia;

  const submit = (e) => {
    e?.preventDefault();
    if (code.length === 5) onJoin(code);
  };

  return (
    <div className="join-panel">
      <p className="join-title">[ JOIN A GAME ]</p>
      <form className="form join-form" onSubmit={submit}>
        <input
          type="text"
          inputMode="latin"
          placeholder="CODE"
          value={code}
          onChange={(e) => setCode(e.target.value.toUpperCase().replace(/[^A-Z0-9]/g, "").slice(0, 5))}
          maxLength={5}
          autoCapitalize="characters"
          autoComplete="off"
          aria-label="lobby code"
        />
        <button type="submit" className="primary" disabled={busy || code.length !== 5}>
          {busy ? "..." : "join"}
        </button>
      </form>
      <div className="join-or">— or —</div>
      <button type="button" className="ghost join-scan" onClick={() => setScanning(true)} disabled={busy}>
        scan the host's QR code
      </button>
      {!canScan && (
        <p className="soon">// no in-app scanner in this browser — your camera app reads the QR and opens the game.</p>
      )}
      {error && <div className="error">{error}</div>}
      <button type="button" className="ghost" onClick={onBack} disabled={busy}>back</button>

      {scanning && (
        <QrScanner
          supported={canScan}
          onCode={(c) => { setScanning(false); setCode(c); onJoin(c); }}
          onClose={() => setScanning(false)}
        />
      )}
    </div>
  );
}

function QrScanner({ supported, onCode, onClose }) {
  const videoRef = useRef(null);
  const [err, setErr] = useState(null);

  useEffect(() => {
    if (!supported) return;
    let stream = null;
    let raf = 0;
    let alive = true;
    const detector = new window.BarcodeDetector({ formats: ["qr_code"] });
    (async () => {
      try {
        stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment" }, audio: false });
        if (!alive) { stream.getTracks().forEach((t) => t.stop()); return; }
        const v = videoRef.current;
        v.srcObject = stream;
        await v.play();
        const tick = async () => {
          if (!alive) return;
          try {
            const codes = await detector.detect(v);
            for (const c of codes) {
              const found = codeFromScan(c.rawValue);
              if (found) { onCode(found); return; }
            }
          } catch { /* frame not ready */ }
          raf = requestAnimationFrame(tick);
        };
        raf = requestAnimationFrame(tick);
      } catch (e) {
        setErr(e?.name === "NotAllowedError" ? "camera permission was denied." : "couldn't open the camera.");
      }
    })();
    return () => {
      alive = false;
      cancelAnimationFrame(raf);
      if (stream) stream.getTracks().forEach((t) => t.stop());
    };
  }, [supported, onCode]);

  return (
    <div className="modal-backdrop" role="dialog" aria-label="scan a QR code">
      <div className="modal terminal scan-modal" onClick={(e) => e.stopPropagation()}>
        <p className="join-title">[ SCAN ]</p>
        {supported ? (
          <>
            <video ref={videoRef} className="scan-video" muted playsInline />
            <p className="soon">// point at the host's QR code</p>
          </>
        ) : (
          <p className="soon">// this browser has no in-app scanner. open your camera app instead — the QR opens the game with the code filled in.</p>
        )}
        {err && <div className="error">{err}</div>}
        <button type="button" className="ghost" onClick={onClose}>close</button>
      </div>
    </div>
  );
}
