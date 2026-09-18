import { useState } from "react";

// End-of-game recap (phase 29): "how the AI played you". The payoff screen — the AI's
// lines round by round next to the prompts, every accusation and how it went, and the
// style notes the AI was carrying on each player. Plus a share button that hands the
// OS share sheet a result card (PNG) when it can, or the text + link otherwise.

const SITE = "https://turing-the-other-cheek.vercel.app";

function parseNotes(json) {
  try {
    const obj = JSON.parse(json);
    if (!obj || typeof obj !== "object") return [];
    return Object.entries(obj)
      .filter(([, v]) => v !== null && v !== "" && !(Array.isArray(v) && v.length === 0))
      .slice(0, 6)
      .map(([k, v]) => [
        k.replace(/([A-Z])/g, " $1").replace(/_/g, " ").toLowerCase(),
        Array.isArray(v) ? v.slice(0, 4).join(", ") : typeof v === "number" ? String(Math.round(v * 100) / 100) : String(v),
      ]);
  } catch {
    return [];
  }
}

export function outcomeLine(ended, myName) {
  const detector = ended.winType === "Detector";
  if (ended.winType === "HumansHidden") return "we kept the AI guessing";
  if (ended.winType === "AiGuesser") return "the AI read the room";
  if (detector && ended.winnerName === myName) return `I caught the AI (it was "${ended.aiRealIdentityName}")`;
  if (detector) return `${ended.winnerName} caught the AI (it was "${ended.aiRealIdentityName}")`;
  return `the AI got away as "${ended.aiRealIdentityName}"`;
}

// 1200x630 result card, drawn on a canvas in the game's CRT palette.
async function renderCard(ended, myName) {
  const c = document.createElement("canvas");
  c.width = 1200; c.height = 630;
  const g = c.getContext("2d");
  g.fillStyle = "#050805";
  g.fillRect(0, 0, c.width, c.height);
  // bezel + phosphor glow
  g.strokeStyle = "#1f5a2c"; g.lineWidth = 6;
  g.strokeRect(24, 24, c.width - 48, c.height - 48);
  const grad = g.createRadialGradient(600, 0, 50, 600, 0, 900);
  grad.addColorStop(0, "rgba(51,255,102,0.16)"); grad.addColorStop(1, "rgba(0,0,0,0)");
  g.fillStyle = grad; g.fillRect(0, 0, c.width, c.height);

  const mono = "bold 44px 'Courier New', monospace";
  g.textBaseline = "top";
  g.shadowColor = "#33ff66"; g.shadowBlur = 18;
  g.fillStyle = "#33ff66"; g.font = "bold 58px 'Courier New', monospace";
  g.fillText("[ TURING THE OTHER CHEEK ]", 70, 80);
  g.shadowBlur = 0;

  const detector = ended.winType === "Detector";
  const headline = ended.winType === "HumansHidden" ? "THE HUMANS STAYED HIDDEN"
    : ended.winType === "AiGuesser" ? "THE AI READ THE ROOM"
    : detector ? "DETECTOR WINS" : "THE AI SURVIVES";
  g.fillStyle = detector || ended.winType === "HumansHidden" ? "#33ff66" : "#ff3b4e";
  g.font = "bold 72px 'Courier New', monospace";
  g.fillText(headline, 70, 190);

  g.fillStyle = "#c8e6c9"; g.font = mono;
  const line = outcomeLine(ended, myName);
  // naive wrap
  const words = line.split(" "); let row = ""; let y = 310;
  for (const w of words) {
    const test = row ? row + " " + w : w;
    if (g.measureText(test).width > 1040) { g.fillText(row, 70, y); y += 56; row = w; }
    else row = test;
  }
  if (row) g.fillText(row, 70, y);

  g.fillStyle = "#7fa88a"; g.font = "32px 'Courier New', monospace";
  const rounds = ended.prompts?.length || 0;
  g.fillText(`${rounds} round${rounds === 1 ? "" : "s"}  ·  ${ended.accusations?.length || 0} accusation${ended.accusations?.length === 1 ? "" : "s"}${ended.isSolo ? "  ·  solo demo" : ""}`, 70, 470);
  g.fillStyle = "#33ff66";
  g.fillText("can you spot the machine?  " + SITE.replace("https://", ""), 70, 540);

  return new Promise((resolve) => c.toBlob(resolve, "image/png"));
}

export default function EndRecap({ ended, myName }) {
  const [shareMsg, setShareMsg] = useState(null);
  const aiLines = (ended.fullTranscript || []).filter((m) => m.isAi);
  const prompts = ended.prompts || [];
  const accusations = ended.accusations || [];
  const notes = (ended.styleNotes || []).map((n) => ({ name: n.displayName, rows: parseNotes(n.notesJson) })).filter((n) => n.rows.length > 0);
  const reverse = ended.winType === "AiGuesser" || ended.winType === "HumansHidden";

  const share = async () => {
    const text = `${outcomeLine(ended, myName)} — Turing the Other Cheek. one player in every lobby is secretly an AI. can you spot the machine?`;
    try {
      const blob = await renderCard(ended, myName);
      const file = blob ? new File([blob], "turing-result.png", { type: "image/png" }) : null;
      if (file && navigator.canShare && navigator.canShare({ files: [file] })) {
        await navigator.share({ title: "Turing the Other Cheek", text, url: SITE, files: [file] });
        return;
      }
      if (navigator.share) {
        await navigator.share({ title: "Turing the Other Cheek", text, url: SITE });
        return;
      }
      // desktop without a share sheet: download the card + copy the text
      if (blob) {
        const a = document.createElement("a");
        a.href = URL.createObjectURL(blob);
        a.download = "turing-result.png";
        a.click();
        setTimeout(() => URL.revokeObjectURL(a.href), 5000);
      }
      await navigator.clipboard?.writeText(`${text} ${SITE}`);
      setShareMsg("saved the card + copied the text");
    } catch (e) {
      if (e && e.name === "AbortError") return; // user closed the sheet
      try { await navigator.clipboard?.writeText(`${text} ${SITE}`); setShareMsg("copied to clipboard"); }
      catch { setShareMsg("couldn't share on this device"); }
    }
    setTimeout(() => setShareMsg(null), 2200);
  };

  return (
    <div className="recap">
      {ended.isSolo && (
        <div className="reveal-box small solo-note">
          solo demo — nothing saved. the real thing is 3–8 phones in a room, and the AI learns how you all type.
        </div>
      )}

      {!reverse && aiLines.length > 0 && (
        <>
          <h3 className="section">how the AI played it</h3>
          <div className="recap-rounds">
            {prompts.map((p) => {
              const ai = aiLines.find((m) => m.round === p.round);
              return (
                <div key={p.round} className="recap-row">
                  <span className="ln-round">r{p.round}</span>
                  <div className="recap-body">
                    <div className="recap-prompt">{p.prompt}</div>
                    <div className="recap-ai">{ai ? ai.text : "—"}</div>
                  </div>
                </div>
              );
            })}
          </div>
        </>
      )}

      {accusations.length > 0 && (
        <>
          <h3 className="section">the accusations</h3>
          <div className="recap-acc">
            {accusations.map((a, i) => (
              <div key={i} className={`recap-row acc-${a.outcome}`}>
                <span className="ln-round">r{a.round}</span>
                <span className="recap-acc-text">
                  <b>{a.accuser}</b> accused <b>{a.accused}</b>
                  {a.outcome === "correct" && <span className="acc-tag good"> — caught it</span>}
                  {a.outcome === "wrong" && <span className="acc-tag bad"> — wrong, burned a token</span>}
                  {a.outcome === "vetoed" && <span className="acc-tag"> — FAKE-OUT by {a.vetoer}, never revealed</span>}
                </span>
              </div>
            ))}
          </div>
        </>
      )}

      {notes.length > 0 && (
        <>
          <h3 className="section">what the AI had on you</h3>
          <p className="soon">// the style notes it played with this game. it gets more of these every time you play.</p>
          <div className="notes">
            {notes.map((n) => (
              <div key={n.name} className="note">
                <div className="note-name">{n.name}</div>
                {n.rows.map(([k, v]) => (
                  <div key={k} className="note-row"><span className="note-k">{k}</span><span className="note-v">{v}</span></div>
                ))}
              </div>
            ))}
          </div>
        </>
      )}

      <div className="share-row">
        <button type="button" className="ghost" onClick={share}>share result</button>
        {shareMsg && <span className="soon">{shareMsg}</span>}
      </div>
    </div>
  );
}
