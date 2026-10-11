import { createContext, useContext, useState, useCallback } from "react";
import { API_BASE } from "../api/config";
import { isInstalledApp } from "../pwa/displayMode";

const AuthContext = createContext(null);

function parseJwt(token) {
  try {
    return JSON.parse(atob(token.split(".")[1]));
  } catch {
    return null;
  }
}

function isTokenExpired(token) {
  const payload = parseJwt(token);
  if (!payload?.exp) return true;
  return Date.now() / 1000 > payload.exp;
}

// Where the JWT lives between loads depends on how the game is running (ADR 0004, 0012):
// - browser tab: sessionStorage. Memory-only sounded clean but phones killed it (mobile
//   browsers discard background tabs constantly), and sessionStorage is per-tab and dies
//   with it, so a shared phone or borrowed laptop doesn't stay logged in for a month.
// - installed app: localStorage. iOS kills a backgrounded home-screen app outright, which
//   wipes sessionStorage, so every relaunch would land on the login screen. The installed
//   app is the player's own phone, so the shared-device argument doesn't apply.
// Either way, expired tokens are rejected on load.
const TOKEN_KEY = "ttoc-token";

function tokenStore() {
  return isInstalledApp() ? localStorage : sessionStorage;
}

function loadStoredToken() {
  try {
    const store = tokenStore();
    const t = store.getItem(TOKEN_KEY);
    if (t && !isTokenExpired(t)) return t;
    if (t) store.removeItem(TOKEN_KEY);
  } catch { /* private mode / storage blocked — stay memory-only */ }
  return null;
}

function storeToken(t) {
  try {
    if (t) tokenStore().setItem(TOKEN_KEY, t);
    else {
      // log out of both, in case the same origin was used as a tab and as the app
      sessionStorage.removeItem(TOKEN_KEY);
      localStorage.removeItem(TOKEN_KEY);
    }
  } catch { /* private mode / storage blocked — stay memory-only */ }
}

export function AuthProvider({ children }) {
  const [token, setTokenState] = useState(loadStoredToken);
  const setToken = useCallback((t) => {
    storeToken(t);
    setTokenState(t);
  }, []);

  const login = useCallback(async (username, password) => {
    const res = await fetch(`${API_BASE}/api/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password }),
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      throw new Error(data.error || "Login failed");
    }
    const data = await res.json();
    setToken(data.token);
    return data;
  }, [setToken]);

  // Guest login: username only, no password. Same name later resumes the same account.
  const guestLogin = useCallback(async (username) => {
    const res = await fetch(`${API_BASE}/api/auth/guest`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username }),
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      throw new Error(data.error || (data.errors && data.errors[0]) || "Guest login failed");
    }
    const data = await res.json();
    setToken(data.token);
    return data;
  }, [setToken]);

  // Drop a JWT straight in (OAuth callback hands us one via the URL fragment).
  const applyToken = useCallback((t) => setToken(t), [setToken]);

  // Claim a display name for the signed-in account (fresh OAuth users). Free name -> set;
  // a prior guest's name -> the server merges that guest in and hands back a fresh token.
  const chooseUsername = useCallback(async (username, currentToken) => {
    const res = await fetch(`${API_BASE}/api/profile/username`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${currentToken}` },
      body: JSON.stringify({ username }),
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      throw new Error(data.error || (data.errors && data.errors[0]) || "couldn't set that name");
    }
    const data = await res.json();
    setToken(data.token);
    return data;
  }, [setToken]);

  const register = useCallback(async (username, displayName, password) => {
    const res = await fetch(`${API_BASE}/api/auth/register`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, displayName, password }),
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      throw new Error(data.error || (data.errors && data.errors[0]) || "Register failed");
    }
    const data = await res.json();
    setToken(data.token);
    return data;
  }, [setToken]);

  const logout = useCallback(() => setToken(null), [setToken]);

  const user = token && !isTokenExpired(token) ? parseJwt(token) : null;

  return (
    <AuthContext.Provider value={{ token, user, login, register, guestLogin, applyToken, chooseUsername, logout, apiBase: API_BASE }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  return useContext(AuthContext);
}
