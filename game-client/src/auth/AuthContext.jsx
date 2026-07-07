import { createContext, useContext, useState, useCallback } from "react";

const AuthContext = createContext(null);

const API_BASE = import.meta.env.VITE_API_URL || "http://localhost:5222";

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

export function AuthProvider({ children }) {
  // JWT lives in memory only. Refresh reloads state, so nothing persists a stale token.
  const [token, setToken] = useState(null);

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
  }, []);

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
  }, []);

  const logout = useCallback(() => setToken(null), []);

  const user = token && !isTokenExpired(token) ? parseJwt(token) : null;

  return (
    <AuthContext.Provider value={{ token, user, login, register, logout, apiBase: API_BASE }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  return useContext(AuthContext);
}
