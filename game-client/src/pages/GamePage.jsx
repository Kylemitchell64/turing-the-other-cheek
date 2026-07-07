import { useEffect, useState, useRef } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { useLobby } from "../game/LobbyContext";

// A live UTC-deadline countdown. The server owns the clock; we just show the gap.
function useCountdown(deadlineUtc) {
  const [secs, setSecs] = useState(0);
  useEffect(() => {
    if (!deadlineUtc) { setSecs(0); return; }
    const end = new Date(deadlineUtc).getTime();
    const tick = () => setSecs(Math.max(0, Math.ceil((end - Date.now()) / 1000)));
    tick();
    const id = setInterval(tick, 250);
    return () => clearInterval(id);
  }, [deadlineUtc]);
  return secs;
}

// Phase 3 game screen. Functional, not polished (polish is phase 5): shows the
// prompt + countdown, an answer box, revealed answers, accuse buttons, the veto
// prompt, the fake-out shake overlay, an end screen with the AI reveal, and a basic
// event log so the whole loop is manually testable.
export default function GamePage() {
  const { user } = useAuth();
  const {
    roster, phase, round, reveal, accusation, accusationMade,
    vetoWindow, fakeOut, resolved, eliminated, ended, events,
    leaveLobby, submitAnswer, makeAccusation, useFakeOut, startGame,
  } = useLobby();
  const navigate = useNavigate();

  const [answer, setAnswer] = useState("");
  const [submitted, setSubmitted] = useState(false);
  const [msg, setMsg] = useState(null);
  const lastRoundRef = useRef(0);

  const myName = user?.displayName || user?.unique_name;

  useEffect(() => {
    if (!roster) navigate("/", { replace: true });
  }, [roster, navigate]);

  // Reset the answer box each new round.
  useEffect(() => {
    if (round && round.number !== lastRoundRef.current) {
      lastRoundRef.current = round.number;
      setAnswer("");
      setSubmitted(false);
      setMsg(null);
    }
  }, [round]);

  const promptSecs = useCountdown(phase === "prompting" ? round?.deadlineUtc : null);
  const accuseSecs = useCountdown(phase === "accusing" ? accusation?.deadlineUtc : null);
  const vetoSecs = useCountdown(phase === "veto" ? vetoWindow?.deadlineUtc : null);

  if (!roster) return null;

  const onLeave = async () => {
    await leaveLobby();
    navigate("/", { replace: true });
  };

  const onSubmit = async (e) => {
    e.preventDefault();
    if (!answer.trim()) return;
    try {
      await submitAnswer(answer.trim());
      setSubmitted(true);
      setMsg(null);
    } catch (err) {
      setMsg(err.message || "couldn't submit");
    }
  };

  const onAccuse = async (name) => {
    try {
      await makeAccusation(name);
      setMsg(null);
    } catch (err) {
      setMsg(err.message || "couldn't accuse");
    }
  };

  const onVeto = async () => {
    try { await useFakeOut(); }
    catch (err) { setMsg(err.message || "couldn't veto"); }
  };

  const onRematch = async () => {
    try { await startGame(); }
    catch (err) { setMsg(err.message || "couldn't restart"); }
  };

  // Who can I accuse? Everyone in the roster except me.
  const accusable = roster.filter((p) => p.displayName !== myName);
  // During a priority window, only the named player may accuse.
  const iHavePriority = !accusation?.priorityName || accusation.priorityName === myName;

  return (
    <div className={`screen ${fakeOut ? "shake" : ""}`}>
      {fakeOut && (
        <div className="fakeout-overlay">
          <div className="fakeout-text">{fakeOut.vetoer} used a FAKE-OUT!</div>
        </div>
      )}

      <div className="topbar">
        <span className="who">{myName}</span>
        <button className="ghost" onClick={onLeave}>leave</button>
      </div>

      {/* END SCREEN */}
      {phase === "ended" && ended ? (
        <div className="panel">
          <h1 className="glow">{ended.winType === "Detector" ? "DETECTOR WINS" : "THE AI SURVIVES"}</h1>
          <p className="tagline">
            {ended.winType === "Detector"
              ? `${ended.winnerName} caught the machine.`
              : "nobody caught it in time."}
          </p>
          <div className="reveal-box">
            the AI was <b className="ai-name">{ended.aiRealIdentityName}</b>
          </div>

          <h3 className="section">transcript</h3>
          <div className="transcript">
            {ended.fullTranscript.map((m, i) => (
              <div key={i} className={m.isAi ? "line ai" : "line"}>
                <span className="ln-round">r{m.round}</span>
                <span className="ln-name">{m.displayName}</span>
                <span className="ln-text">{m.text}</span>
              </div>
            ))}
          </div>

          <button className="primary" onClick={onRematch}>rematch</button>
          <button className="ghost" onClick={onLeave}>back home</button>
        </div>
      ) : (
        <div className="panel">
          <h1 className="glow">ROUND {round?.number ?? "-"}</h1>

          {/* PROMPT + ANSWER */}
          {phase === "prompting" && round && (
            <>
              <p className="tagline">answer fast — {promptSecs}s</p>
              <div className="prompt-box">{round.prompt}</div>
              {submitted ? (
                <p className="soon">// answer locked in. waiting for the reveal…</p>
              ) : (
                <form className="form" onSubmit={onSubmit}>
                  <input
                    value={answer}
                    onChange={(e) => setAnswer(e.target.value)}
                    maxLength={280}
                    placeholder="type something human…"
                    autoFocus
                  />
                  <button className="primary" type="submit" disabled={!answer.trim()}>
                    submit
                  </button>
                </form>
              )}
            </>
          )}

          {/* REVEAL + ACCUSE */}
          {(phase === "revealing" || phase === "accusing" || phase === "veto") && reveal && (
            <>
              <p className="tagline">"{reveal.prompt}"</p>
              <div className="roster">
                {reveal.answers.map((a, i) => {
                  const canAccuse =
                    phase === "accusing" &&
                    a.displayName !== myName &&
                    iHavePriority &&
                    !eliminated.includes(myName);
                  return (
                    <div key={i} className="answer-row">
                      <div className="answer-head">
                        <span className="seat-name">{a.displayName}</span>
                        {canAccuse && (
                          <button className="accuse-btn" onClick={() => onAccuse(a.displayName)}>
                            accuse
                          </button>
                        )}
                      </div>
                      <div className="answer-text">{a.text}</div>
                    </div>
                  );
                })}
              </div>

              {phase === "accusing" && (
                <p className="soon">
                  {accusation?.priorityName
                    ? `// ${accusation.priorityName} has priority — ${accuseSecs}s`
                    : `// accusation window — ${accuseSecs}s`}
                </p>
              )}
              {phase === "revealing" && <p className="soon">// reveal… accusation window opens next</p>}
            </>
          )}

          {/* ACCUSATION MADE (everyone sees) */}
          {accusationMade && phase !== "prompting" && (
            <div className="reveal-box small">
              {accusationMade.accuser} accused {accusationMade.accused}
            </div>
          )}

          {/* VETO PROMPT (only eligible token-holders get vetoWindow) */}
          {phase === "veto" && vetoWindow && (
            <div className="veto-box">
              <p>use a fake-out to overrule {accusationMade?.accuser} and keep the game going?</p>
              <button className="primary danger-btn" onClick={onVeto}>
                FAKE-OUT ({vetoSecs}s)
              </button>
            </div>
          )}

          {/* RESOLUTION (only when NOT vetoed) */}
          {resolved && (
            <div className={`reveal-box ${resolved.correct ? "good" : "bad"}`}>
              {resolved.accuser} → {resolved.accused}: {resolved.correct ? "correct!" : "wrong"}
            </div>
          )}

          {msg && <div className="error">{msg}</div>}
        </div>
      )}

      {/* EVENT LOG — manual-testing aid, will be dropped/polished in phase 5 */}
      <div className="panel log">
        <h3 className="section">event log</h3>
        <div className="log-lines">
          {events.slice().reverse().map((e) => (
            <div key={e.t + e.msg} className="log-line">{e.msg}</div>
          ))}
        </div>
      </div>
    </div>
  );
}
