import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { useLobby } from "../game/LobbyContext";
import { needsCreator } from "../auth/firstUse";
import MaintenanceBanner from "../components/MaintenanceBanner";

// Shown once per device before the very first guest play so nobody is surprised that a
// guest name can get swept. After that, PLAY goes straight through.
const QUICK_PLAY_SEEN = "quickPlaySeen";

export default function LoginPage() {
  const { guestLogin, apiBase } = useAuth();
  const { joinLobby } = useLobby();
  const navigate = useNavigate();

  const [guestName, setGuestName] = useState("");
  const [guestCode, setGuestCode] = useState("");
  const [providers, setProviders] = useState({ google: false, github: false });
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);
  const [showModal, setShowModal] = useState(false);

  // Which OAuth buttons to show — driven entirely by the server flag.
  useEffect(() => {
    let alive = true;
    fetch(`${apiBase}/api/auth/providers`)
      .then((r) => (r.ok ? r.json() : null))
      .then((data) => { if (alive && data) setProviders(data); })
      .catch(() => {});
    return () => { alive = false; };
  }, [apiBase]);

  const anyOAuth = providers.google || providers.github;

  const doGuestLogin = async () => {
    setShowModal(false);
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

  const onPlay = (e) => {
    e.preventDefault();
    if (guestName.trim().length < 3) return;
    // First time on this device: explain guest vs sign-in before diving in.
    let seen = false;
    try { seen = localStorage.getItem(QUICK_PLAY_SEEN) === "1"; } catch { seen = false; }
    if (!seen) { setShowModal(true); return; }
    doGuestLogin();
  };

  const continueAsGuest = () => {
    try { localStorage.setItem(QUICK_PLAY_SEEN, "1"); } catch { /* private mode */ }
    doGuestLogin();
  };

  const oauth = (provider) => {
    window.location.href = `${apiBase}/api/auth/${provider}/login`;
  };

  return (
    <div className="screen center">
      <MaintenanceBanner />
      <div className="panel">
        <h1 className="glow">TURING THE OTHER CHEEK</h1>
        <p className="tagline">one of you isn't human. find them.</p>

        {/* Fastest path: name + optional code, one tap. */}
        <form onSubmit={onPlay} className="form">
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
            {busy ? "..." : "PLAY"}
          </button>
        </form>

        {anyOAuth && (
          <div className="oauth">
            <div className="divider"><span>sign in &amp; save everything</span></div>
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
      </div>

      {showModal && (
        <div className="modal-backdrop" onClick={() => setShowModal(false)}>
          <div className="modal terminal" onClick={(e) => e.stopPropagation()}>
            <h2 className="modal-head">[ QUICK PLAY ]</h2>
            <p className="modal-copy">
              playing as a guest saves your character and a light style profile, so the AI
              remembers you a bit. heads up: names left unused for 30 days get wiped.
            </p>
            <p className="modal-copy">
              sign in instead and everything sticks around for good — it also trains a
              sharper impostor against you, and never expires.
            </p>
            <button type="button" className="primary big" onClick={continueAsGuest} disabled={busy}>
              CONTINUE AS GUEST
            </button>
            {anyOAuth ? (
              <button
                type="button"
                className="ghost"
                onClick={() => { setShowModal(false); oauth(providers.google ? "google" : "github"); }}
                disabled={busy}
              >
                SIGN IN INSTEAD
              </button>
            ) : (
              <button type="button" className="link" onClick={() => setShowModal(false)}>
                maybe later
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
