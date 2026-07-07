import { useEffect, useState, useRef } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { useLobby } from "../game/LobbyContext";
import CountdownRing from "../components/CountdownRing";
import AccuseButton from "../components/AccuseButton";

// Token pips for a player badge. Shows current tokens out of 3.
function Tokens({ n }) {
  return (
    <span className="tokens" title={`${n} fake-out token${n === 1 ? "" : "s"}`}>
      {[0, 1, 2].map((i) => (
        <span key={i} className={i < n ? "pip on" : "pip"} />
      ))}
    </span>
  );
}

export default function GamePage() {
  const { user } = useAuth();
  const {
    roster, phase, round, reveal, accusation, accusationMade,
    vetoWindow, fakeOut, resolved, eliminated, ended, history,
    tokens, clockSkew,
    leaveLobby, submitAnswer, makeAccusation, useFakeOut: sendFakeOut, startGame,
  } = useLobby();
  const navigate = useNavigate();

  const [answer, setAnswer] = useState("");
  const [submitted, setSubmitted] = useState(false);
  const [msg, setMsg] = useState(null);
  const [vetoed, setVetoed] = useState(false);
  const lastRoundRef = useRef(0);
  const scrollRef = useRef(null);

  const myName = user?.displayName || user?.unique_name;

  useEffect(() => {
    if (!roster) navigate("/", { replace: true });
  }, [roster, navigate]);

  // Reset per-round answer/veto state on a new round.
  useEffect(() => {
    if (round && round.number !== lastRoundRef.current) {
      lastRoundRef.current = round.number;
      setAnswer("");
      setSubmitted(false);
      setVetoed(false);
      setMsg(null);
    }
  }, [round]);

  // Keep the chat scrollback pinned to the newest round.
  useEffect(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [history, phase, reveal]);

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
    try { await sendFakeOut(); setVetoed(true); }
    catch (err) { setMsg(err.message || "couldn't veto"); }
  };

  const onRematch = async () => {
    try { await startGame(); }
    catch (err) { setMsg(err.message || "couldn't restart"); }
  };

  const myTokens = tokens[myName] ?? 0;
  const iAmEliminated = eliminated.includes(myName);
  // During a priority window only the named player may accuse.
  const iHavePriority = !accusation?.priorityName || accusation.priorityName === myName;
  const canIAccuse = phase === "accusing" && iHavePriority && !iAmEliminated && myTokens > 0;

  // The current round's revealed answers (for the live accuse cards), if any.
  const current = reveal;

  return (
    <div className={`screen game ${fakeOut ? "shake" : ""}`}>
      {fakeOut && (
        <div className="fakeout-overlay">
          <div className="fakeout-flash" />
          <div className="fakeout-text">{fakeOut.vetoer} used a FAKE-OUT!</div>
        </div>
      )}

      <div className="topbar">
        <span className="who">{myName} <Tokens n={myTokens} /></span>
        <button className="ghost" onClick={onLeave}>leave</button>
      </div>

      {/* END SCREEN */}
      {phase === "ended" && ended ? (
        <EndScreen
          ended={ended}
          myName={myName}
          onRematch={onRematch}
          onLeave={onLeave}
          msg={msg}
        />
      ) : (
        <>
          {/* ROSTER STRIP with live token badges */}
          <div className="roster-strip">
            {roster.map((p) => {
              const t = tokens[p.displayName] ?? p.tokensRemaining;
              const out = eliminated.includes(p.displayName);
              const isMe = p.displayName === myName;
              return (
                <div key={p.displayName} className={`chip${out ? " out" : ""}${isMe ? " me" : ""}`}>
                  <span className="chip-name">{p.displayName}{isMe ? " (you)" : ""}</span>
                  <Tokens n={t} />
                </div>
              );
            })}
          </div>

          {/* CHAT SCROLLBACK */}
          <div className="panel chat" ref={scrollRef}>
            {history.length === 0 && phase === "prompting" && (
              <p className="soon">// first prompt is up — answer below.</p>
            )}
            {history.map((h) => (
              <div key={h.round} className="chat-round">
                <div className="chat-prompt">
                  <span className="chat-r">round {h.round}</span> {h.prompt}
                </div>
                {h.answers.map((a, i) => (
                  <div key={i} className={`chat-msg${a.displayName === myName ? " mine" : ""}`}>
                    <span className="chat-author">{a.displayName}</span>
                    <span className="chat-text">{a.text}</span>
                  </div>
                ))}
              </div>
            ))}

            {/* Live accusation banner (everyone sees who accused whom) */}
            {accusationMade && phase !== "prompting" && (
              <div className="chat-system">
                {accusationMade.accuser} accused {accusationMade.accused}
              </div>
            )}
            {resolved && (
              <div className={`chat-system ${resolved.correct ? "good" : "bad"}`}>
                {resolved.accuser} → {resolved.accused}: {resolved.correct ? "CORRECT — caught it!" : "wrong. token burned."}
              </div>
            )}
          </div>

          {/* ACTION DOCK — prompt/answer, timer ring, accuse, veto */}
          <div className="panel dock">
            {phase === "prompting" && round && (
              <div className="dock-row">
                <CountdownRing
                  deadlineUtc={round.deadlineUtc}
                  skewMs={clockSkew}
                  label="answer"
                />
                <div className="dock-main">
                  <div className="prompt-box">{round.prompt}</div>
                  {submitted ? (
                    <div className="answer-locked">
                      <span className="lock-ico">✓</span> answer locked in. waiting for the reveal…
                    </div>
                  ) : (
                    <form className="answer-form" onSubmit={onSubmit}>
                      <input
                        value={answer}
                        onChange={(e) => setAnswer(e.target.value)}
                        maxLength={280}
                        placeholder="type something human…"
                        autoFocus
                      />
                      <div className="answer-form-foot">
                        <span className="counter">{answer.length}/280</span>
                        <button className="primary" type="submit" disabled={!answer.trim()}>
                          submit
                        </button>
                      </div>
                    </form>
                  )}
                </div>
              </div>
            )}

            {phase === "revealing" && (
              <p className="soon">// answers are in. accusation window opens next…</p>
            )}

            {phase === "accusing" && current && (
              <div className="accuse-panel">
                <div className="dock-row">
                  <CountdownRing
                    deadlineUtc={accusation?.deadlineUtc}
                    skewMs={clockSkew}
                    label={accusation?.priorityName ? "priority" : "accuse"}
                  />
                  <div className="dock-main">
                    <p className="accuse-hint">
                      {iAmEliminated
                        ? "// you're out of tokens — answer-only."
                        : accusation?.priorityName && !iHavePriority
                          ? `// ${accusation.priorityName} has priority. hold on…`
                          : myTokens <= 0
                            ? "// no tokens left — you can't accuse."
                            : "// spot the machine. hold to accuse."}
                    </p>
                  </div>
                </div>
                <div className="accuse-grid">
                  {current.answers
                    .filter((a) => a.displayName !== myName)
                    .map((a, i) => (
                      <div key={i} className="accuse-card">
                        <div className="accuse-card-head">
                          <span className="seat-name">{a.displayName}</span>
                          <Tokens n={tokens[a.displayName] ?? 3} />
                        </div>
                        <div className="accuse-card-text">{a.text}</div>
                        <AccuseButton
                          name={a.displayName}
                          disabled={!canIAccuse}
                          onConfirm={onAccuse}
                        />
                      </div>
                    ))}
                </div>
              </div>
            )}

            {phase === "veto" && (
              <div className="veto-box">
                <div className="dock-row">
                  <CountdownRing
                    deadlineUtc={vetoWindow?.deadlineUtc}
                    skewMs={clockSkew}
                    label="veto"
                    size={80}
                  />
                  <div className="dock-main">
                    {vetoWindow ? (
                      vetoed ? (
                        <p className="veto-copy">// fake-out sent. keeping the game alive.</p>
                      ) : (
                        <>
                          <p className="veto-copy">
                            use a fake-out to overrule <b>{accusationMade?.accuser}</b> and keep the
                            game going? the result stays hidden.
                          </p>
                          <button className="primary danger-btn" onClick={onVeto}>
                            spend a token — FAKE-OUT
                          </button>
                        </>
                      )
                    ) : (
                      <p className="veto-copy">
                        // {accusationMade?.accuser} accused {accusationMade?.accused}. waiting to see
                        if anyone vetoes…
                      </p>
                    )}
                  </div>
                </div>
              </div>
            )}

            {msg && <div className="error">{msg}</div>}
          </div>
        </>
      )}
    </div>
  );
}

// End screen: winner banner, AI reveal, transcript with the AI's lines highlighted,
// a stat-deltas placeholder, and a rematch button (host restarts the same lobby).
function EndScreen({ ended, myName, onRematch, onLeave, msg }) {
  const detector = ended.winType === "Detector";
  const iWon = detector && ended.winnerName === myName;

  return (
    <div className="panel end">
      <div className={`banner ${detector ? "banner-detector" : "banner-ai"}`}>
        <h1 className="glow">{detector ? "DETECTOR WINS" : "THE AI SURVIVES"}</h1>
        <p className="tagline">
          {detector
            ? iWon
              ? "you caught the machine."
              : `${ended.winnerName} caught the machine.`
            : "nobody caught it in time."}
        </p>
      </div>

      <div className="reveal-box big">
        the AI was <b className="ai-name">{ended.aiRealIdentityName}</b>
      </div>

      <h3 className="section">the whole transcript — its lines glow red</h3>
      <div className="transcript">
        {ended.fullTranscript.map((m, i) => (
          <div key={i} className={m.isAi ? "line ai" : "line"}>
            <span className="ln-round">r{m.round}</span>
            <span className="ln-name">{m.displayName}</span>
            <span className="ln-text">{m.text}</span>
          </div>
        ))}
      </div>

      <h3 className="section">stat deltas</h3>
      <div className="reveal-box small">
        {detector && iWon
          ? "+1 detector win"
          : detector
            ? "the game goes to the detector."
            : "the machine walked. +1 witnessed escape."}
        {" "}// full stats tracking lands with the next update.
      </div>

      {msg && <div className="error">{msg}</div>}
      <button className="primary" onClick={onRematch}>rematch (same lobby)</button>
      <button className="ghost" onClick={onLeave}>back home</button>
    </div>
  );
}
