import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { useLobby } from "../game/LobbyContext";
import { needsCreator } from "../auth/firstUse";

export default function LoginPage() {
  const { login, register, guestLogin, apiBase } = useAuth();
  const { joinLobby } = useLobby();
  const navigate = useNavigate();

  // Guest is the default, fastest path. "classic" collapses the password form.
  const [guestName, setGuestName] = useState("");
  const [guestCode, setGuestCode] = useState("");

  const [showClassic, setShowClassic] = useState(false);
  const [mode, setMode] = useState("login"); // "login" | "register"
  const [username, setUsername] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");

  const [providers, setProviders] = useState({ google: false, github: false });
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);

  // Which OAuth buttons to show — driven entirely by the server flag.
  useEffect(() => {
    let alive = true;
    fetch(`${apiBase}/api/auth/providers`)
      .then((r) => (r.ok ? r.json() : null))
      .then((data) => { if (alive && data) setProviders(data); })
      .catch(() => {});
    return () => { alive = false; };
  }, [apiBase]);

  const playAsGuest = async (e) => {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const data = await guestLogin(guestName.trim());
      const code = guestCode.trim().toUpperCase();
      // First-use: brand-new player with no saved character → make one first, stashing
      // the pending lobby code so it auto-joins after they save/skip.
      if (await needsCreator(data.token, data.displayName || data.username)) {
        navigate("/character", { state: { pendingCode: code || null } });
        return;
      }
      if (code) {
        // Auto-join straight into the lobby via the existing join flow. The hub's
        // accessTokenFactory is lazy, so it reads the just-set guest token on connect.
        await joinLobby(code);
        navigate("/lobby");
      } else {
        navigate("/");
      }
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  };

  const submitClassic = async (e) => {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const data = mode === "login"
        ? await login(username.trim(), password)
        : await register(username.trim(), displayName.trim(), password);
      if (await needsCreator(data.token, data.displayName || data.username)) {
        navigate("/character");
        return;
      }
      navigate("/");
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  };

  const oauth = (provider) => {
    window.location.href = `${apiBase}/api/auth/${provider}/login`;
  };

  const anyOAuth = providers.google || providers.github;

  return (
    <div className="screen center">
      <div className="panel">
        <h1 className="glow">TURING THE OTHER CHEEK</h1>
        <p className="tagline">one of you isn't human. find them.</p>

        {/* Fastest path: username + optional code, one tap. */}
        <form onSubmit={playAsGuest} className="form">
          <input
            type="text"
            placeholder="pick a name"
            value={guestName}
            onChange={(e) => setGuestName(e.target.value)}
            autoComplete="username"
            maxLength={20}
            required
          />
          <input
            type="text"
            placeholder="lobby code (optional)"
            value={guestCode}
            onChange={(e) => setGuestCode(e.target.value.toUpperCase())}
            maxLength={5}
            autoCapitalize="characters"
          />
          {error && <div className="error">{error}</div>}
          <button type="submit" className="primary big" disabled={busy || guestName.trim().length < 3}>
            {busy ? "..." : "PLAY AS GUEST"}
          </button>
        </form>

        {anyOAuth && (
          <div className="oauth">
            <div className="divider"><span>or</span></div>
            {providers.google && (
              <button type="button" className="ghost" onClick={() => oauth("google")} disabled={busy}>
                sign in with Google
              </button>
            )}
            {providers.github && (
              <button type="button" className="ghost" onClick={() => oauth("github")} disabled={busy}>
                sign in with GitHub
              </button>
            )}
          </div>
        )}

        <div className="divider"><span></span></div>
        <button
          type="button"
          className="link"
          onClick={() => { setShowClassic((s) => !s); setError(null); }}
        >
          {showClassic ? "hide classic login" : "classic login"}
        </button>

        {showClassic && (
          <>
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

            <form onSubmit={submitClassic} className="form">
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
              <button type="submit" className="primary" disabled={busy}>
                {busy ? "..." : mode === "login" ? "log in" : "create account"}
              </button>
            </form>
          </>
        )}
      </div>
    </div>
  );
}
