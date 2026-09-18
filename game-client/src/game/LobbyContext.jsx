import { createContext, useContext, useRef, useState, useCallback, useEffect } from "react";
import { buildGameConnection } from "./gameConnection";
import { measureClockSkew } from "../api/client";
import { DEFAULT_PACK } from "./packs";
import { useAuth } from "../auth/AuthContext";

const LobbyContext = createContext(null);

// Owns the single SignalR connection and all lobby + live-game state. Screens
// (Home, Lobby, Game) read from here so create/join/start/play share one socket.
export function LobbyProvider({ children }) {
  const { token } = useAuth();
  const connRef = useRef(null);

  const [status, setStatus] = useState("idle"); // idle | connecting | connected | error
  const [lobby, setLobby] = useState(null); // { code, state, players[], crewName? }
  const [crewCode, setCrewCode] = useState(null); // a crew's persistent code, when in a crew lobby
  const [roster, setRoster] = useState(null); // set when GameStarted fires
  const [error, setError] = useState(null);

  // Live round state (phase 3).
  const [round, setRound] = useState(null); // { number, prompt, deadlineUtc }
  const [phase, setPhase] = useState(null); // prompting | revealing | accusing | veto | ended
  const [reveal, setReveal] = useState(null); // { round, prompt, answers[] }
  // Reverse mode (phase 22): the anonymous shuffled answers shown while the AI "analyzes"
  // ({ round, prompt, answers:[{id,text}] }), and the attributions once they land
  // ({ round, guesses:[{answerId,guessedName,correct,actualName,taunt}], roundCorrect, ... }).
  const [reverseReveal, setReverseReveal] = useState(null);
  const [aiGuesses, setAiGuesses] = useState(null);
  const [accusation, setAccusation] = useState(null); // { deadlineUtc, priorityName } while a window is open
  const [accusationMade, setAccusationMade] = useState(null); // { accuser, accused }
  const [vetoWindow, setVetoWindow] = useState(null); // { deadlineUtc } — only the eligible get this
  const [fakeOut, setFakeOut] = useState(null); // { vetoer } — drives the shake overlay
  const [resolved, setResolved] = useState(null); // { correct, accuser, accused }
  const [eliminated, setEliminated] = useState([]); // display names knocked out of accusing
  const [wrongAccusers, setWrongAccusers] = useState([]); // names who accused wrong (unvetoed) — drives end-screen deltas
  const [ended, setEnded] = useState(null); // { winType, winnerName, aiRealIdentityName, fullTranscript[], accusations[], styleNotes[], prompts[], isSolo }
  // Phase 29: after a Rejoin snapshot says we already answered the current round, this holds
  // that round number so the Game screen shows "sent" instead of an empty box.
  const [answeredRound, setAnsweredRound] = useState(null);
  // Operator cheat (phase 30): the AI's name, delivered privately to an admin seat when the
  // console has "reveal AI" on. Null for everyone else, always.
  const [cheatAiName, setCheatAiName] = useState(null);
  const [events, setEvents] = useState([]); // a simple scrolling event log for manual testing

  // Host-picked lobby options (pack / impostor difficulty / answer pace). Seeded from
  // LobbyUpdated for late joiners, kept live via LobbyOptionsChanged.
  const [packKey, setPackKey] = useState(DEFAULT_PACK);
  const [difficulty, setDifficulty] = useState("normal");
  const [paceKey, setPaceKey] = useState("standard");
  // Game mode (phase 22): "classic" (hidden impostor) or "reverse" (no impostor — the AI
  // reads everyone and guesses who wrote what). Host-picked pre-start.
  const [mode, setMode] = useState("classic");
  // The AI-built custom pack's title when packKey === "custom" (phase 20); null otherwise.
  const [customPackName, setCustomPackName] = useState(null);

  // The room's chiptune mood (phase 21). Host-driven + cosmetic. Seeded from LobbyUpdated
  // for late joiners, kept live via LobbyMusicChanged. MusicContext reads this to drive
  // every "follow host" player onto one shared soundtrack.
  const [musicMood, setMusicMood] = useState("arcade");

  // Who's currently shown as "typing" this round, keyed by display name. Driven by the
  // server's PlayerTyping(name, isTyping) — humans (via SetTyping) AND the AI's faked
  // indicator, indistinguishable here. Cleared on every phase change.
  const [typing, setTyping] = useState({});

  // Live per-player token counts, keyed by display name. Seeded from the roster at
  // GameStarted (everyone starts with 3), then adjusted as tokens are spent: a veto
  // costs the vetoer one, elimination means zero. The server never streams a live
  // token map, so we mirror it from the events that imply a change.
  const [tokens, setTokens] = useState({});

  // Estimated client clock skew in ms (client - server). Countdowns subtract this so
  // they run off the server's UTC deadlines regardless of a wrong phone clock.
  const [clockSkew, setClockSkew] = useState(0);

  // Chat-style scrollback: each revealed round appended so the Game screen reads like
  // a transcript instead of only showing the latest round.
  const [history, setHistory] = useState([]); // [{ round, prompt, answers[] }]

  const log = useCallback((msg) => {
    setEvents((prev) => [...prev.slice(-40), { t: Date.now(), msg }]);
  }, []);

  // Keep the latest token in a ref so accessTokenFactory always reads a fresh one.
  // Assigned during render (not in an effect) so a join fired immediately after login
  // — e.g. the guest "join with code" path — always sees the just-set token.
  const tokenRef = useRef(token);
  tokenRef.current = token;

  // Last typing state we told the server, so we only send on CHANGES (never spam a
  // keystroke stream). Reset to false at the start of each prompting round.
  const typingSentRef = useRef(false);

  // Lazily stand up the connection + wire every server→client event on first use.
  const ensureConnected = useCallback(async () => {
    if (!connRef.current) {
      const conn = buildGameConnection(() => tokenRef.current || "");

      conn.on("LobbyUpdated", (state) => {
        setLobby(state);
        // Sync the options for anyone who joined after the host already picked them.
        if (state?.packKey) setPackKey(state.packKey);
        if (state?.difficulty) setDifficulty(state.difficulty);
        if (state?.paceKey) setPaceKey(state.paceKey);
        // customPackName is present only for a custom pack; normal packs send null.
        setCustomPackName(state?.customPackName ?? null);
        // Sync the room's music mood for late joiners (host-driven, cosmetic).
        if (state?.musicMood) setMusicMood(state.musicMood);
        // Sync the game mode for late joiners.
        if (state?.mode) setMode(state.mode);
      });

      conn.on("LobbyMusicChanged", (mood) => {
        setMusicMood(mood);
        log(`host set the music to ${mood}`);
      });

      conn.on("LobbyOptionsChanged", (pack, diff, pace, custom, m) => {
        setPackKey(pack);
        setDifficulty(diff);
        setPaceKey(pace);
        setCustomPackName(custom ?? null);
        if (m) setMode(m);
        log(`options set to ${custom ? `custom:${custom}` : pack} / ${diff} / ${pace} / ${m ?? "classic"}`);
      });

      conn.on("CheatReveal", (name) => setCheatAiName(name));

      conn.on("GameStarted", (r) => {
        setCheatAiName(null);
        setRoster(r);
        setPhase("prompting");
        setEnded(null);
        setReveal(null);
        setReverseReveal(null);
        setAiGuesses(null);
        setEliminated([]);
        setWrongAccusers([]);
        setEvents([]);
        setHistory([]);
        setTyping({});
        typingSentRef.current = false;
        // Seed live token counts from the roster (each entry carries its start count).
        setTokens(Object.fromEntries(r.map((p) => [p.displayName, p.tokensRemaining])));
        log("game started");
      });

      conn.on("PlayerTyping", (name, isTyping) => {
        setTyping((prev) => {
          if (isTyping) {
            if (prev[name]) return prev;
            return { ...prev, [name]: true };
          }
          if (!prev[name]) return prev;
          const next = { ...prev };
          delete next[name];
          return next;
        });
      });

      conn.on("PromptStarted", (prompt, number, deadlineUtc) => {
        setRound({ number, prompt, deadlineUtc });
        setPhase("prompting");
        setReveal(null);
        setAccusation(null);
        setAccusationMade(null);
        setVetoWindow(null);
        setResolved(null);
        setTyping({}); // fresh round, no one's typing yet
        typingSentRef.current = false;
        log(`round ${number}: ${prompt}`);
      });

      conn.on("AnswersRevealed", (payload) => {
        setReveal(payload);
        setPhase("revealing");
        setTyping({}); // prompting over — drop any lingering bubbles
        // Append to the scrollback (guard against a duplicate if the event repeats).
        setHistory((prev) =>
          prev.some((h) => h.round === payload.round) ? prev : [...prev, payload]
        );
        log(`round ${payload.round} answers revealed`);
      });

      // Reverse mode: the shuffled anonymous answers, shown with an "analyzing" beat while
      // we wait for the AI's guesses.
      conn.on("ReverseRevealStarted", (payload) => {
        setReverseReveal(payload);
        setAiGuesses(null);
        setPhase("revealing");
        setTyping({}); // prompting over — drop any lingering bubbles
        log(`round ${payload.round}: AI is analyzing ${payload.answers.length} answers`);
      });

      // Reverse mode: the AI's attributions land — who it guessed, whether it was right, and
      // the taunt. Also carries the running accuracy tally.
      conn.on("AiGuessesRevealed", (payload) => {
        setAiGuesses(payload);
        log(`round ${payload.round}: AI got ${payload.roundCorrect}/${payload.roundTotal} (game ${payload.gameCorrect}/${payload.gameTotal})`);
      });

      conn.on("AccusationWindowOpened", (deadlineUtc, priorityName) => {
        setAccusation({ deadlineUtc, priorityName: priorityName || null });
        setPhase("accusing");
        log(priorityName ? `priority window: ${priorityName}` : "accusation window open");
      });

      conn.on("AccusationMade", (accuser, accused) => {
        setAccusationMade({ accuser, accused });
        log(`${accuser} accused ${accused}`);
      });

      conn.on("VetoWindowOpened", (deadlineUtc) => {
        setVetoWindow({ deadlineUtc });
        setPhase("veto");
        log("veto window (you can fake-out)");
      });

      conn.on("FakeOutUsed", (vetoer) => {
        setFakeOut({ vetoer, at: Date.now() });
        setVetoWindow(null);
        // A veto spends one of the vetoer's tokens.
        setTokens((prev) => ({ ...prev, [vetoer]: Math.max(0, (prev[vetoer] ?? 0) - 1) }));
        log(`${vetoer} used a FAKE-OUT`);
        // Clear the shake after the animation (~600ms).
        setTimeout(() => setFakeOut(null), 700);
      });

      conn.on("AccusationResolved", (correct, accuser, accused) => {
        setResolved({ correct, accuser, accused });
        setVetoWindow(null);
        // A wrong, unvetoed accusation burns one of the accuser's tokens and, if the
        // AI ends up surviving, counts as a "times fooled" for that accuser.
        if (!correct) {
          setTokens((prev) => ({ ...prev, [accuser]: Math.max(0, (prev[accuser] ?? 0) - 1) }));
          setWrongAccusers((prev) => (prev.includes(accuser) ? prev : [...prev, accuser]));
        }
        log(`resolved: ${accuser} → ${accused} was ${correct ? "CORRECT" : "wrong"}`);
      });

      // Phase 31: server-driven token changes that aren't implied by another event (today:
      // the no-answer penalty — every second blank round costs a token).
      conn.on("TokensChanged", (name, remaining, reason) => {
        setTokens((prev) => ({ ...prev, [name]: remaining }));
        log(`${name}: ${remaining} token${remaining === 1 ? "" : "s"} (${reason})`);
      });

      conn.on("PlayerEliminated", (name) => {
        setEliminated((prev) => (prev.includes(name) ? prev : [...prev, name]));
        setTokens((prev) => ({ ...prev, [name]: 0 }));
        log(`${name} is out of tokens (answer-only)`);
      });

      conn.on("GameEnded", (payload) => {
        setEnded(payload);
        setPhase("ended");
        log(`game over — ${payload.winType}`);
      });

      // ---- phase 29: survive a dropped socket (phone lock, tab sleep, flaky wifi) ----
      // SignalR's auto-reconnect brings the socket back, but with a NEW connection id the
      // server no longer maps it to our seat or lobby group. Rejoin re-attaches and returns
      // a full snapshot; applyResync rebuilds the screen from it.
      conn.onreconnecting(() => setStatus("reconnecting"));
      conn.onreconnected(async () => {
        setStatus("connected");
        await rejoinRef.current();
      });
      conn.onclose(() => {
        // Only mark disconnected if we didn't tear it down ourselves (leaveLobby nulls the ref).
        if (connRef.current === conn) setStatus("disconnected");
      });

      connRef.current = conn;
    }

    const conn = connRef.current;
    if (conn.state === "Disconnected") {
      setStatus("connecting");
      await conn.start();
      setStatus("connected");
      // Estimate clock skew once we're online so countdowns track server deadlines.
      measureClockSkew().then(setClockSkew).catch(() => {});
    }
    return conn;
  }, [log]);

  // Apply a Rejoin snapshot (see ResyncDto on the server). Mirrors what the live events
  // would have set, minus anything that has already passed.
  const applyResync = useCallback((snap) => {
    if (!snap) return;
    setLobby(snap.lobby);
    if (snap.lobby?.packKey) setPackKey(snap.lobby.packKey);
    if (snap.lobby?.difficulty) setDifficulty(snap.lobby.difficulty);
    if (snap.lobby?.paceKey) setPaceKey(snap.lobby.paceKey);
    if (snap.lobby?.mode) setMode(snap.lobby.mode);
    if (snap.lobby?.musicMood) setMusicMood(snap.lobby.musicMood);
    setCustomPackName(snap.lobby?.customPackName ?? null);

    const started = snap.phase !== "Lobby";
    if (!started) {
      setRoster(null);
      setPhase(null);
      setRound(null);
      setEnded(null);
      return;
    }
    setRoster(snap.roster || null);
    setCheatAiName(snap.cheatAiName || null);
    setTokens(snap.tokens || {});
    setEliminated(snap.eliminated || []);
    setHistory(snap.history || []);
    setReveal(snap.reveal || null);
    setTyping({});
    setRound({ number: snap.round, prompt: snap.prompt, deadlineUtc: snap.deadlineUtc });
    setAnsweredRound(snap.answeredThisRound ? snap.round : null);
    setAccusation(null);
    setAccusationMade(null);
    setVetoWindow(null);
    setResolved(null);
    switch (snap.phase) {
      case "Prompting":
        setPhase("prompting");
        setEnded(null);
        break;
      case "Revealing":
        setPhase("revealing");
        break;
      case "Accusing":
        setPhase("accusing");
        if (snap.accusationOpen) setAccusation({ deadlineUtc: snap.deadlineUtc, priorityName: snap.priorityName || null });
        break;
      case "VetoWindow":
        setAccusationMade(snap.accuser ? { accuser: snap.accuser, accused: snap.accused } : null);
        if (snap.canVetoNow) {
          setVetoWindow({ deadlineUtc: snap.deadlineUtc });
          setPhase("veto");
        } else {
          setPhase("accusing");
        }
        break;
      case "Ended":
        setEnded(snap.ended || null);
        setPhase("ended");
        break;
      default:
        break;
    }
    log("reconnected — state resynced");
  }, [log]);

  // Rejoin on the current connection. Safe to call any time; a user without a seat gets null.
  const rejoinRef = useRef(async () => {});
  rejoinRef.current = async () => {
    const conn = connRef.current;
    if (!conn || conn.state !== "Connected") return;
    try {
      const snap = await conn.invoke("Rejoin");
      if (snap) applyResync(snap);
    } catch { /* the next event will catch us up */ }
  };

  // A phone coming back from the lock screen: the socket may be gone for good (auto
  // reconnect gave up while backgrounded). Kick it: start a fresh connection and Rejoin.
  useEffect(() => {
    const onVisible = async () => {
      if (document.visibilityState !== "visible") return;
      const conn = connRef.current;
      if (!conn || !lobby) return;
      if (conn.state === "Disconnected") {
        try {
          setStatus("connecting");
          await conn.start();
          setStatus("connected");
          await rejoinRef.current();
        } catch { setStatus("disconnected"); }
      } else if (conn.state === "Connected") {
        // Still up, but we may have missed events while asleep — cheap to resync.
        await rejoinRef.current();
      }
    };
    document.addEventListener("visibilitychange", onVisible);
    window.addEventListener("online", onVisible);
    return () => {
      document.removeEventListener("visibilitychange", onVisible);
      window.removeEventListener("online", onVisible);
    };
  }, [lobby]);

  const createLobby = useCallback(async () => {
    setError(null);
    setRoster(null);
    const conn = await ensureConnected();
    await conn.invoke("CreateLobby");
  }, [ensureConnected]);

  const joinLobby = useCallback(async (code) => {
    setError(null);
    setRoster(null);
    const conn = await ensureConnected();
    await conn.invoke("JoinLobby", code.trim().toUpperCase());
  }, [ensureConnected]);

  // Open (or fold into) a crew's live lobby. Seeded server-side from the crew's saved
  // config; lands the caller in the normal LobbyPage, which shows the crew name + code.
  const createCrewLobby = useCallback(async (crewId, crewJoinCode = null) => {
    setError(null);
    setRoster(null);
    setCrewCode(crewJoinCode); // the persistent code to show instead of the live one
    const conn = await ensureConnected();
    await conn.invoke("CreateCrewLobby", crewId);
  }, [ensureConnected]);

  const startGame = useCallback(async () => {
    setError(null);
    const conn = await ensureConnected();
    await conn.invoke("StartGame");
  }, [ensureConnected]);

  // Solo demo (phase 29): alone in the lobby, seat three bot stand-ins + the AI and go.
  const startSoloGame = useCallback(async () => {
    setError(null);
    const conn = await ensureConnected();
    await conn.invoke("StartSoloGame");
  }, [ensureConnected]);

  // One-tap from Home: create a lobby and immediately start it solo.
  const playSolo = useCallback(async () => {
    setError(null);
    setRoster(null);
    const conn = await ensureConnected();
    await conn.invoke("CreateLobby");
    await conn.invoke("StartSoloGame");
  }, [ensureConnected]);

  // Host picks the lobby options pre-start (pack / difficulty / pace / mode). Any omitted
  // arg keeps its current value. Optimistically update locally; the server echoes
  // LobbyOptionsChanged to everyone (including us).
  const setLobbyOptions = useCallback(async ({ pack, diff, pace, mode: m } = {}) => {
    const p = pack ?? packKey;
    const d = diff ?? difficulty;
    const pc = pace ?? paceKey;
    const md = m ?? mode;
    setPackKey(p);
    setDifficulty(d);
    setPaceKey(pc);
    setMode(md);
    setCustomPackName(null); // picking a normal pack clears any custom one
    const conn = await ensureConnected();
    await conn.invoke("SetLobbyOptions", p, d, pc, md);
  }, [ensureConnected, packKey, difficulty, paceKey, mode]);

  // Install an AI-built custom pack from its signed share-code. The server decodes +
  // verifies it (a tampered code throws), sets packKey="custom", and echoes
  // LobbyOptionsChanged with the pack name. Throws with the server's message on failure.
  const setCustomPack = useCallback(async (code) => {
    const conn = await ensureConnected();
    await conn.invoke("SetCustomPack", code);
  }, [ensureConnected]);

  // Host sets the room's chiptune mood (phase 21). Cosmetic, so the server allows it in any
  // state. Optimistic; the server echoes LobbyMusicChanged to everyone (including us).
  const setLobbyMusic = useCallback(async (mood) => {
    setMusicMood(mood);
    const conn = await ensureConnected();
    await conn.invoke("SetLobbyMusic", mood);
  }, [ensureConnected]);

  const submitAnswer = useCallback(async (text) => {
    const conn = await ensureConnected();
    await conn.invoke("SubmitAnswer", text);
  }, [ensureConnected]);

  // Tell the server whether we're typing an answer. Throttled to state CHANGES only —
  // the server re-broadcasts as PlayerTyping during Prompting. Best-effort (a dropped
  // typing ping is cosmetic).
  const setTypingState = useCallback(async (isTyping) => {
    if (typingSentRef.current === isTyping) return;
    typingSentRef.current = isTyping;
    try {
      const conn = connRef.current;
      if (conn && conn.state === "Connected") await conn.invoke("SetTyping", isTyping);
    } catch { /* cosmetic — ignore */ }
  }, []);

  const makeAccusation = useCallback(async (accusedName) => {
    const conn = await ensureConnected();
    await conn.invoke("MakeAccusation", accusedName);
  }, [ensureConnected]);

  const useFakeOut = useCallback(async () => {
    const conn = await ensureConnected();
    await conn.invoke("UseFakeOut");
  }, [ensureConnected]);

  // Leave a lobby / game: tear the socket down so the server drops our seat, and
  // wipe all local game state.
  const leaveLobby = useCallback(async () => {
    setLobby(null);
    setCrewCode(null);
    setRoster(null);
    setRound(null);
    setPhase(null);
    setReveal(null);
    setReverseReveal(null);
    setAiGuesses(null);
    setAccusation(null);
    setAccusationMade(null);
    setVetoWindow(null);
    setResolved(null);
    setEliminated([]);
    setWrongAccusers([]);
    setEnded(null);
    setAnsweredRound(null);
    setCheatAiName(null);
    setEvents([]);
    setHistory([]);
    setTokens({});
    setTyping({});
    typingSentRef.current = false;
    setPackKey(DEFAULT_PACK);
    setDifficulty("normal");
    setPaceKey("standard");
    setMode("classic");
    setCustomPackName(null);
    setMusicMood("arcade");
    if (connRef.current) {
      try { await connRef.current.stop(); } catch { /* ignore */ }
      connRef.current = null;
      setStatus("idle");
    }
  }, []);

  const value = {
    status, lobby, crewCode, roster, error, setError,
    round, phase, reveal, reverseReveal, aiGuesses, accusation, accusationMade,
    vetoWindow, fakeOut, resolved, eliminated, wrongAccusers, ended, events, answeredRound, cheatAiName,
    tokens, clockSkew, history, packKey, difficulty, paceKey, mode, customPackName, typing,
    musicMood, setLobbyMusic,
    createLobby, joinLobby, createCrewLobby, setCrewCode, startGame, startSoloGame, playSolo, setLobbyOptions, setCustomPack, leaveLobby,
    submitAnswer, makeAccusation, useFakeOut, setTypingState,
  };

  return <LobbyContext.Provider value={value}>{children}</LobbyContext.Provider>;
}

export function useLobby() {
  return useContext(LobbyContext);
}
