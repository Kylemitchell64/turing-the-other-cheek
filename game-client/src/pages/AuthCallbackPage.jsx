import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";

// Landing spot for the OAuth redirect. The API sends the browser here with the JWT (or
// an error) in the URL fragment: /auth/callback#token=... . We parse it, store the token
// in memory, strip the fragment from the URL (so the token never sticks in history), and
// bounce to the home screen.
const ERRORS = {
  denied: "sign-in was cancelled",
  state: "sign-in expired, try again",
  exchange: "couldn't reach the provider, try again",
  account: "couldn't set up your account",
  provider: "that sign-in method isn't available",
};

export default function AuthCallbackPage() {
  const { applyToken } = useAuth();
  const navigate = useNavigate();
  const [error, setError] = useState(null);

  useEffect(() => {
    const hash = window.location.hash.startsWith("#")
      ? window.location.hash.slice(1)
      : window.location.hash;
    const params = new URLSearchParams(hash);
    const token = params.get("token");
    const err = params.get("error");

    // Strip the fragment so the token isn't left in the address bar / history.
    window.history.replaceState(null, "", window.location.pathname);

    if (token) {
      applyToken(token);
      navigate("/", { replace: true });
    } else {
      setError(ERRORS[err] || "sign-in failed");
    }
  }, [applyToken, navigate]);

  return (
    <div className="screen center">
      <div className="panel">
        <h1 className="glow">TURING THE OTHER CHEEK</h1>
        {error ? (
          <>
            <div className="error">{error}</div>
            <button className="primary" onClick={() => navigate("/login", { replace: true })}>
              back to login
            </button>
          </>
        ) : (
          <p className="tagline">signing you in...</p>
        )}
      </div>
    </div>
  );
}
