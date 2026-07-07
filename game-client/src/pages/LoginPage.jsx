import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";

export default function LoginPage() {
  const { login, register } = useAuth();
  const navigate = useNavigate();

  const [mode, setMode] = useState("login"); // "login" | "register"
  const [username, setUsername] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e) => {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      if (mode === "login") {
        await login(username.trim(), password);
      } else {
        await register(username.trim(), displayName.trim(), password);
      }
      navigate("/");
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="screen center">
      <div className="panel">
        <h1 className="glow">TURING THE OTHER CHEEK</h1>
        <p className="tagline">one of you isn't human. find them.</p>

        <div className="tabs">
          <button
            className={mode === "login" ? "tab active" : "tab"}
            onClick={() => { setMode("login"); setError(null); }}
            type="button"
          >
            log in
          </button>
          <button
            className={mode === "register" ? "tab active" : "tab"}
            onClick={() => { setMode("register"); setError(null); }}
            type="button"
          >
            sign up
          </button>
        </div>

        <form onSubmit={submit} className="form">
          <input
            type="text"
            placeholder="username"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoComplete="username"
            required
          />
          {mode === "register" && (
            <input
              type="text"
              placeholder="display name"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              required
            />
          )}
          <input
            type="password"
            placeholder="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete={mode === "login" ? "current-password" : "new-password"}
            required
          />

          {error && <div className="error">{error}</div>}

          <button type="submit" className="primary" disabled={busy}>
            {busy ? "..." : mode === "login" ? "log in" : "create account"}
          </button>
        </form>
      </div>
    </div>
  );
}
