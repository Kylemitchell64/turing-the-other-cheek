import { useRef, useState, useCallback, useEffect } from "react";

const HOLD_MS = 800;

// Long-press-to-accuse. Press and hold (~800ms) to confirm; a fill sweeps across the
// button as it charges. Works for touch AND mouse (pointer events cover both).
// Releasing early cancels — this makes accusing a deliberate act, not a mistap.
export default function AccuseButton({ name, disabled, onConfirm }) {
  const [progress, setProgress] = useState(0); // 0..1
  const [armed, setArmed] = useState(false);
  const rafRef = useRef(0);
  const startRef = useRef(0);
  const firedRef = useRef(false);

  const stop = useCallback(() => {
    cancelAnimationFrame(rafRef.current);
    rafRef.current = 0;
    setArmed(false);
    setProgress(0);
  }, []);

  useEffect(() => () => cancelAnimationFrame(rafRef.current), []);

  const begin = useCallback(
    (e) => {
      if (disabled) return;
      e.preventDefault();
      firedRef.current = false;
      startRef.current = performance.now();
      setArmed(true);
      const step = (now) => {
        const p = Math.min(1, (now - startRef.current) / HOLD_MS);
        setProgress(p);
        if (p >= 1) {
          if (!firedRef.current) {
            firedRef.current = true;
            onConfirm(name);
          }
          stop();
          return;
        }
        rafRef.current = requestAnimationFrame(step);
      };
      rafRef.current = requestAnimationFrame(step);
    },
    [disabled, name, onConfirm, stop]
  );

  return (
    <button
      type="button"
      className={`accuse-hold${armed ? " arming" : ""}`}
      disabled={disabled}
      onPointerDown={begin}
      onPointerUp={stop}
      onPointerLeave={stop}
      onPointerCancel={stop}
      // Don't let a long-press trigger the OS text-selection / context menu on mobile.
      onContextMenu={(e) => e.preventDefault()}
      style={{ touchAction: "none" }}
      aria-label={`hold to accuse ${name}`}
    >
      <span className="accuse-fill" style={{ width: `${progress * 100}%` }} />
      <span className="accuse-label">
        {armed ? "hold…" : "accuse"}
      </span>
    </button>
  );
}
