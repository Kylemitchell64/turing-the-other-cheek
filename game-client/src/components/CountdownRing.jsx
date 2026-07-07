import { useEffect, useRef, useState } from "react";

// Animated countdown ring driven by a server UTC deadline (not a client-started
// timer). `skewMs` is the estimated client-minus-server clock offset — we subtract it
// so the ring tracks the real server deadline even if the phone's clock is off.
// The SVG stroke-dashoffset sweeps as time runs out.
export default function CountdownRing({ deadlineUtc, totalMs, skewMs = 0, size = 96, label }) {
  const [remaining, setRemaining] = useState(0);
  const startTotalRef = useRef(totalMs);

  useEffect(() => {
    if (!deadlineUtc) { setRemaining(0); return; }
    const end = new Date(deadlineUtc).getTime();

    const compute = () => {
      // Server "now" ≈ client now minus our measured skew.
      const serverNow = Date.now() - skewMs;
      return Math.max(0, end - serverNow);
    };

    // If no explicit total was given, infer it from the first reading so the ring
    // starts full and drains — good enough for the visual sweep.
    if (!totalMs) startTotalRef.current = compute() || 1;
    else startTotalRef.current = totalMs;

    setRemaining(compute());
    const id = setInterval(() => setRemaining(compute()), 100);
    return () => clearInterval(id);
  }, [deadlineUtc, totalMs, skewMs]);

  const total = startTotalRef.current || 1;
  const frac = Math.max(0, Math.min(1, remaining / total));
  const secs = Math.ceil(remaining / 1000);

  const stroke = 6;
  const r = (size - stroke) / 2;
  const c = 2 * Math.PI * r;
  const offset = c * (1 - frac);
  const low = secs <= 5;

  return (
    <div className="ring-wrap" role="timer" aria-label={label || "time remaining"}>
      <svg width={size} height={size} className={low ? "ring low" : "ring"}>
        <circle
          className="ring-track"
          cx={size / 2}
          cy={size / 2}
          r={r}
          strokeWidth={stroke}
          fill="none"
        />
        <circle
          className="ring-progress"
          cx={size / 2}
          cy={size / 2}
          r={r}
          strokeWidth={stroke}
          fill="none"
          strokeLinecap="round"
          strokeDasharray={c}
          strokeDashoffset={offset}
          transform={`rotate(-90 ${size / 2} ${size / 2})`}
        />
        <text x="50%" y="50%" className="ring-secs" dominantBaseline="central" textAnchor="middle">
          {secs}
        </text>
      </svg>
      {label && <div className="ring-label">{label}</div>}
    </div>
  );
}
