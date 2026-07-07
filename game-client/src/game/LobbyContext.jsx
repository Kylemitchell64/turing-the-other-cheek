import { createContext, useContext, useRef, useState, useCallback, useEffect } from "react";
import { buildGameConnection } from "./gameConnection";
import { useAuth } from "../auth/AuthContext";

const LobbyContext = createContext(null);

// Owns the single SignalR connection and all lobby + live-game state. Screens
// (Home, Lobby, Game) read from here so create/join/start/play share one socket.
export function LobbyProvider({ children }) {
  const { token } = useAuth();
  const connRef = useRef(null);

  const [status, setStatus] = useState("idle"); // idle | connecting | connected | error
  const [lobby, setLobby] = useState(null); // { code, state, players[] }
  const [roster, setRoster] = useState(null); // set when GameStarted fires
  const [error, setError] = useState(null);

  // Live round state (phase 3).
  const [round, setRound] = useState(null); // { number, prompt, deadlineUtc }
  const [phase, setPhase] = useState(null); // prompting | revealing | accusing | veto | ended
  const [reveal, setReveal] = useState(null); // { round, prompt, answers[] }
  const [accusation, setAccusation] = useState(null); // { deadlineUtc, priorityName } while a window is open
  const [accusationMade, setAccusationMade] = useState(null); // { accuser, accused }
  const [vetoWindow, setVetoWindow] = useState(null); // { deadlineUtc } — only the eligible get this
  const [fakeOut, setFakeOut] = useState(null); // { vetoer } — drives the shake overlay
  const [resolved, setResolved] = useState(null); // { correct, accuser, accused }
  const [eliminated, setEliminated] = useState([]); // display names knocked out of accusing
  const [ended, setEnded] = useState(null); // { winType, winnerName, aiRealIdentityName, fullTranscript[] }
  const [events, setEvents] = useState([]); // a simple scrolling event log for manual testing

  const log = useCallback((msg) => {
    setEvents((prev) => [...prev.slice(-40), { t: Date.now(), msg }]);
  }, []);

  // Keep the latest token in a ref so accessTokenFactory always reads a fresh one.
  const tokenRef = useRef(token);
  useEffect(() => { tokenRef.current = token; }, [token]);

  // Lazily stand up the connection + wire every server→client event on first use.
  const ensureConnected = useCallback(async () => {
    if (!connRef.current) {
      const conn = buildGameConnection(() => tokenRef.current || "");

      conn.on("LobbyUpdated", (state) => setLobby(state));

      conn.on("GameStarted", (r) => {
        setRoster(r);
        setPhase("prompting");
        setEnded(null);
        setReveal(null);
        setEliminated([]);
        setEvents([]);
        log("game started");
      });

      conn.on("PromptStarted", (prompt, number, deadlineUtc) => {
        setRound({ number, prompt, deadlineUtc });
        setPhase("prompting");
        setReveal(null);
        setAccusation(null);
        setAccusationMade(null);
        setVetoWindow(null);
        setResolved(null);
        log(`round ${number}: ${prompt}`);
      });

      conn.on("AnswersRevealed", (payload) => {
        setReveal(payload);
        setPhase("revealing");
        log(`round ${payload.round} answers revealed`);
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
        log(`${vetoer} used a FAKE-OUT`);
        // Clear the shake after the animation (~600ms).
        setTimeout(() => setFakeOut(null), 700);
      });

      conn.on("AccusationResolved", (correct, accuser, accused) => {
        setResolved({ correct, accuser, accused });
        setVetoWindow(null);
        log(`resolved: ${accuser} → ${accused} was ${correct ? "CORRECT" : "wrong"}`);
      });

      conn.on("PlayerEliminated", (name) => {
        setEliminated((prev) => (prev.includes(name) ? prev : [...prev, name]));
        log(`${name} is out of tokens (answer-only)`);
      });

      conn.on("GameEnded", (payload) => {
        setEnded(payload);
        setPhase("ended");
        log(`game over — ${payload.winType}`);
      });

      connRef.current = conn;
    }

    const conn = connRef.current;
    if (conn.state === "Disconnected") {
      setStatus("connecting");
      await conn.start();
      setStatus("connected");
    }
    return conn;
  }, [log]);

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

  const startGame = useCallback(async () => {
    setError(null);
    const conn = await ensureConnected();
    await conn.invoke("StartGame");
  }, [ensureConnected]);

  const submitAnswer = useCallback(async (text) => {
    const conn = await ensureConnected();
    await conn.invoke("SubmitAnswer", text);
  }, [ensureConnected]);

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
    setRoster(null);
    setRound(null);
    setPhase(null);
    setReveal(null);
    setAccusation(null);
    setAccusationMade(null);
    setVetoWindow(null);
    setResolved(null);
    setEliminated([]);
    setEnded(null);
    setEvents([]);
    if (connRef.current) {
      try { await connRef.current.stop(); } catch { /* ignore */ }
      connRef.current = null;
      setStatus("idle");
    }
  }, []);

  const value = {
    status, lobby, roster, error, setError,
    round, phase, reveal, accusation, accusationMade,
    vetoWindow, fakeOut, resolved, eliminated, ended, events,
    createLobby, joinLobby, startGame, leaveLobby,
    submitAnswer, makeAccusation, useFakeOut,
  };

  return <LobbyContext.Provider value={value}>{children}</LobbyContext.Provider>;
}

export function useLobby() {
  return useContext(LobbyContext);
}
