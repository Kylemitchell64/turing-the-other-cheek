import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { needsCreator } from "../auth/firstUse";

// Where a brand-new OAuth account lands before anything else. Same name rules as guest
// login (3-20, letters/numbers/underscore). Picking a name that a guest already used
// "claims" that guest identity server-side (its character/profile/samples/stats fold in);
// a name owned by another real account is rejected. On success we continue the normal
// first-use chain (character creator, then home).
const NAME_RE = /^[A-Za-z0-9_]{3,20}$/;

export default function ChooseUsernamePage() {
  const { chooseUsername, token } = useAuth();
  const navigate = useNavigate();

  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);

  const submit = async (e) => {
    e.preventDefault();
    const trimmed = name.trim();
    if (!NAME_RE.test(trimmed)) {
      setError("3-20 letters, numbers or underscores");
      return;
    }
    setError(null);
    setBusy(true);
    try {
      const data = await chooseUsername(trimmed, token);
      // Continue first-use: new accounts make a character before home.
      if (await needsCreator(data.token, data.displayName || data.username)) {
        navigate("/character", { replace: true });
      } else {
        navigate("/", { replace: true });
      }
    } catch (err) {
      setError(err.message);
      setBusy(false);
    }
  };

  return (
    <div className="screen center">
      <div className="panel">
        <h1 className="glow">[ PICK YOUR NAME ]</h1>
        <p className="tagline">
          this is how you'll show up at the table. already played as a guest with this
          name? sign in claims it — you keep everything.<span className="cursor" />
        </p>

        <form onSubmit={submit} className="form">
          <input
            type="text"
            placeholder="username"
            value={name}
            onChange={(e) => setName(e.target.value)}
            autoComplete="username"
            maxLength={20}
            autoFocus
            required
          />
          {error && <div className="error">{error}</div>}
          <button type="submit" className="primary big" disabled={busy || name.trim().length < 3}>
            {busy ? "..." : "CLAIM IT"}
          </button>
        </form>
      </div>
    </div>
  );
}
