import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { useLobby } from "../game/LobbyContext";
import { PACKS, packFor } from "../game/packs";
import PackMakerModal from "../components/PackMakerModal";
import MenuWanderer from "../components/MenuWanderer";
import ReconnectBanner from "../components/ReconnectBanner";

// Impostor difficulty + answer pace options. Keys must match the server's
// DifficultyProfile / PaceOptions keys exactly.
const DIFFICULTIES = [
  { key: "easy", label: "EASY", blurb: "training wheels. the bot answers like a polite houseguest and always takes about the same time. spot the tells." },
  { key: "normal", label: "NORMAL", blurb: "fair fight. it mimics the group but leaves seams if you pay attention." },
  { key: "hard", label: "HARD", blurb: "good luck. full mimicry, fake typos, human timing. it knows how you type." },
];

const PACES = [
  { key: "flash", label: "FLASH 10s", blurb: "blink and its over. short answers only." },
  { key: "quick", label: "QUICK 20s", blurb: "keep it moving." },
  { key: "standard", label: "NORMAL 30s", blurb: "the classic." },
  { key: "relaxed", label: "RELAXED 45s", blurb: "room to think." },
  { key: "snail", label: "SNAIL 60s", blurb: "a full minute per question. for the overthinkers." },
];

// Game mode (phase 22). Keys match the server's GameModes. REVERSE needs everyone signed
// in with some play history — the server enforces that at start.
const MODES = [
  { key: "classic", label: "CLASSIC", blurb: "one of you is secretly the AI. answer, accuse, and don't get fooled." },
  { key: "reverse", label: "REVERSE", blurb: "no impostor — the AI reads everyone's answers and guesses who wrote what. stay unpredictable." },
];

export default function LobbyPage() {
  const { user } = useAuth();
  const { lobby, crewCode, roster, packKey, difficulty, paceKey, mode, customPackName,
    setLobbyOptions, setCustomPack, startGame, startSoloGame, leaveLobby } = useLobby();
  const navigate = useNavigate();

  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState(null);
  const [copied, setCopied] = useState(false);
  const [showMaker, setShowMaker] = useState(false);

  // Join link + QR (phase 29): phones scan it instead of typing the code. Crew rooms keep
  // their own persistent code flow, so no QR there. Hooks live above the early return.
  const [qr, setQr] = useState(null);
  const [shared, setShared] = useState(null);
  const qrCode = lobby ? (lobby.crewName && crewCode ? crewCode : lobby.code) : null;
  const qrIsCrew = !!lobby?.crewName;
  useEffect(() => {
    if (!qrCode || qrIsCrew) { setQr(null); return; }
    let alive = true;
    const url = `${window.location.origin}/login?join=${encodeURIComponent(qrCode)}`;
    // dynamic import: the encoder is ~40KB and only lobby hosts need it
    import("qrcode")
      .then((mod) => (mod.default || mod).toDataURL(url, { margin: 1, width: 220, color: { dark: "#33ff66", light: "#00000000" } }))
      .then((data) => { if (alive) setQr(data); })
      .catch(() => { if (alive) setQr(null); });
    return () => { alive = false; };
  }, [qrCode, qrIsCrew]);

  // No lobby in state (e.g. hard refresh lost the socket) → back to home.
  useEffect(() => {
    if (!lobby && !roster) navigate("/", { replace: true });
  }, [lobby, roster, navigate]);

  // When the host starts, every client gets GameStarted → head to the game screen.
  useEffect(() => {
    if (roster) navigate("/game", { replace: true });
  }, [roster, navigate]);

  if (!lobby) return null;

  const myName = user?.displayName || user?.unique_name;
  const me = lobby.players.find((p) => p.displayName === myName);
  const amHost = me?.isHost;
  const canStart = lobby.players.length >= 3 && lobby.players.length <= 8;

  const isCustom = packKey === "custom";
  const activePack = packFor(packKey);

  // Crew lobby: the server sends only crewName over the wire; we show the crew's
  // persistent code (threaded through context) instead of the ephemeral live one.
  const isCrew = !!lobby.crewName;
  const shownCode = isCrew && crewCode ? crewCode : lobby.code;

  const joinUrl = `${window.location.origin}/login?join=${encodeURIComponent(shownCode)}`;
  const shareInvite = async () => {
    const text = `join my Turing the Other Cheek lobby — code ${shownCode}`;
    try {
      if (navigator.share) { await navigator.share({ title: "Turing the Other Cheek", text, url: joinUrl }); return; }
      await navigator.clipboard.writeText(joinUrl);
      setShared("link copied!");
    } catch (e) {
      if (e && e.name === "AbortError") return;
      try { await navigator.clipboard.writeText(joinUrl); setShared("link copied!"); } catch { setShared(joinUrl); }
    }
    setTimeout(() => setShared(null), 1800);
  };

  const onSolo = async () => {
    setErr(null);
    setBusy(true);
    try {
      await startSoloGame();
    } catch (e) {
      setErr(e.message || "Couldn't start the solo demo");
      setBusy(false);
    }
  };

  const onPickPack = async (key) => {
    if (key === packKey) return;
    try {
      await setLobbyOptions({ pack: key });
    } catch (e) {
      setErr(e.message || "Couldn't change the pack");
    }
  };

  const onPickDifficulty = async (key) => {
    if (key === difficulty) return;
    try {
      await setLobbyOptions({ diff: key });
    } catch (e) {
      setErr(e.message || "Couldn't change the difficulty");
    }
  };

  const onPickPace = async (key) => {
    if (key === paceKey) return;
    try {
      await setLobbyOptions({ pace: key });
    } catch (e) {
      setErr(e.message || "Couldn't change the pace");
    }
  };

  const onPickMode = async (key) => {
    if (key === mode) return;
    try {
      await setLobbyOptions({ mode: key });
    } catch (e) {
      setErr(e.message || "Couldn't change the mode");
    }
  };

  const copyCode = async () => {
    try {
      await navigator.clipboard.writeText(shownCode);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch { /* clipboard blocked — the code is on screen anyway */ }
  };

  const onStart = async () => {
    setErr(null);
    setBusy(true);
    try {
      await startGame();
    } catch (e) {
      setErr(e.message || "Couldn't start");
      setBusy(false);
    }
  };

  const onLeave = async () => {
    await leaveLobby();
    navigate("/", { replace: true });
  };

  return (
    <div className="screen">
      <div className="topbar">
        <span className="who">{myName}</span>
        <button className="ghost" onClick={onLeave}>leave</button>
      </div>

      <ReconnectBanner />

      <div className="panel">
        <h1 className="glow">{isCrew ? `[ CREW: ${lobby.crewName} ]` : "[ LOBBY ]"}</h1>
        <p className="tagline">
          {isCrew ? "your crew's permanent room. 3–8 players." : "share the code. 3–8 players."}
          <span className="cursor" />
        </p>

        <div className="lobby-grid">
        <div className="lobby-col">
        <button className="code" onClick={copyCode} title="tap to copy">
          {shownCode.split("").map((c, i) => (
            <span key={i} className="code-char">{c}</span>
          ))}
        </button>
        <p className="soon">
          {copied ? "copied!" : isCrew ? "// crewmates open this from their crews list" : "// tap the code to copy"}
        </p>
        {!isCrew && (
          <div className="invite">
            {qr && <img className="qr" src={qr} alt={`QR code to join lobby ${shownCode}`} width={110} height={110} />}
            <div className="invite-text">
              <p className="soon">// scan to join, or</p>
              <button type="button" className="ghost invite-btn" onClick={shareInvite}>share invite link</button>
              {shared && <p className="soon">{shared}</p>}
            </div>
          </div>
        )}

        <div className="roster">
          {lobby.players.map((p) => (
            <div key={p.displayName} className={p.isConnected ? "seat" : "seat off"}>
              <span className="dot" />
              <span className="seat-name">
                {p.displayName}{p.displayName === myName ? " (you)" : ""}
              </span>
              {p.isHost && <span className="badge">host</span>}
            </div>
          ))}
        </div>
        <p className="soon">// everyone starts with 3 fake-out tokens</p>
        </div>

        <div className="lobby-col">
        <div className="pack-picker">
          <p className="pack-heading">// prompt pack</p>
          <div className="segmented">
            {PACKS.map((p) => {
              const selected = p.key === packKey;
              const cls = ["seg", selected ? "on" : "", amHost ? "" : "locked"].join(" ").trim();
              return (
                <button
                  key={p.key}
                  className={cls}
                  onClick={() => amHost && onPickPack(p.key)}
                  disabled={!amHost}
                  aria-pressed={selected}
                >
                  {p.label}
                </button>
              );
            })}
            {amHost && (
              <button
                className={["seg", "seg-make", isCustom ? "on" : ""].join(" ").trim()}
                onClick={() => setShowMaker(true)}
                aria-pressed={isCustom}
              >
                + MAKE YOUR OWN
              </button>
            )}
          </div>
          {isCustom ? (
            <>
              <p className="pack-desc">
                <span className="badge custom-chip">CUSTOM: {customPackName || "your pack"}</span>
              </p>
              <p className="pack-age">// an ai-built category, just for this room.</p>
            </>
          ) : (
            <>
              <p className="pack-desc">{activePack.description}</p>
              {activePack.ageNote && <p className="pack-age">{activePack.ageNote}</p>}
            </>
          )}
        </div>

        <div className="pack-picker">
          <p className="pack-heading">// ai difficulty</p>
          <div className="segmented">
            {DIFFICULTIES.map((d) => {
              const selected = d.key === difficulty;
              const cls = ["seg", selected ? "on" : "", amHost ? "" : "locked"].join(" ").trim();
              return (
                <button key={d.key} className={cls} onClick={() => amHost && onPickDifficulty(d.key)}
                  disabled={!amHost} aria-pressed={selected}>
                  {d.label}
                  {d.key === "normal" && <span className="seg-rec">[recommended]</span>}
                </button>
              );
            })}
          </div>
          <p className="pack-desc">{DIFFICULTIES.find((d) => d.key === difficulty)?.blurb}</p>
        </div>

        <div className="pack-picker">
          <p className="pack-heading">// answer pace</p>
          <div className="segmented">
            {PACES.map((p) => {
              const selected = p.key === paceKey;
              const cls = ["seg", selected ? "on" : "", amHost ? "" : "locked"].join(" ").trim();
              return (
                <button key={p.key} className={cls} onClick={() => amHost && onPickPace(p.key)}
                  disabled={!amHost} aria-pressed={selected}>
                  {p.label}
                </button>
              );
            })}
          </div>
          <p className="pack-desc">{PACES.find((p) => p.key === paceKey)?.blurb}</p>
        </div>

        <div className="pack-picker">
          <p className="pack-heading">// game mode</p>
          <div className="segmented">
            {MODES.map((m) => {
              const selected = m.key === mode;
              const cls = ["seg", selected ? "on" : "", amHost ? "" : "locked"].join(" ").trim();
              return (
                <button key={m.key} className={cls} onClick={() => amHost && onPickMode(m.key)}
                  disabled={!amHost} aria-pressed={selected}>
                  {m.label}
                  {m.key === "classic" && <span className="seg-rec">[recommended]</span>}
                </button>
              );
            })}
          </div>
          <p className="pack-desc">{MODES.find((m) => m.key === mode)?.blurb}</p>
          {mode === "reverse" && (
            <p className="pack-age">// everyone must be signed in with some play history</p>
          )}
          {!amHost && <p className="soon">// the host picks the options</p>}
        </div>

        {err && <div className="error">{err}</div>}

        {amHost ? (
          <button className="primary" onClick={onStart} disabled={busy || !canStart}>
            {busy ? "..." : canStart ? "start game" : `need ${Math.max(0, 3 - lobby.players.length)} more`}
          </button>
        ) : (
          <p className="soon">// waiting for the host to start</p>
        )}
        {amHost && !isCrew && lobby.players.length === 1 && (
          <button className="ghost solo-btn" onClick={onSolo} disabled={busy}>
            nobody around? solo demo <span className="seg-rec">[vs bots]</span>
          </button>
        )}
        </div>
        </div>
      </div>

      {showMaker && (
        <PackMakerModal
          onUse={(code) => setCustomPack(code)}
          onClose={() => setShowMaker(false)}
        />
      )}

      <MenuWanderer />
    </div>
  );
}
