import { createContext, useContext, useRef, useState, useCallback, useEffect } from "react";
import { buildGameConnection } from "./gameConnection";
import { useAuth } from "../auth/AuthContext";

const LobbyContext = createContext(null);

// Owns the single SignalR connection and the current lobby/roster state. Screens
// (Home, Lobby, Game) read from here so create/join/start all share one socket.
export function LobbyProvider({ children }) {
  const { token } = useAuth();
  const connRef = useRef(null);

  const [status, setStatus] = useState("idle"); // idle | connecting | connected | error
  const [lobby, setLobby] = useState(null); // { code, state, players[] }
  const [roster, setRoster] = useState(null); // set when GameStarted fires
  const [error, setError] = useState(null);

  // Keep the latest token in a ref so accessTokenFactory always reads a fresh one.
  const tokenRef = useRef(token);
  useEffect(() => { tokenRef.current = token; }, [token]);

  // Lazily stand up the connection on first use.
  const ensureConnected = useCallback(async () => {
    if (connRef.current) {
      if (connRef.current.state === "Connected") return connRef.current;
      // already connecting/reconnecting — wait it out below
    } else {
      const conn = buildGameConnection(() => tokenRef.current || "");
      conn.on("LobbyUpdated", (state) => setLobby(state));
      conn.on("GameStarted", (r) => setRoster(r));
      connRef.current = conn;
    }

    const conn = connRef.current;
    if (conn.state === "Disconnected") {
      setStatus("connecting");
      await conn.start();
      setStatus("connected");
    }
    return conn;
  }, []);

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

  // Leave a lobby: tear the socket down so the server drops our seat.
  const leaveLobby = useCallback(async () => {
    setLobby(null);
    setRoster(null);
    if (connRef.current) {
      try { await connRef.current.stop(); } catch { /* ignore */ }
      connRef.current = null;
      setStatus("idle");
    }
  }, []);

  const value = {
    status, lobby, roster, error, setError,
    createLobby, joinLobby, startGame, leaveLobby,
  };

  return <LobbyContext.Provider value={value}>{children}</LobbyContext.Provider>;
}

export function useLobby() {
  return useContext(LobbyContext);
}
