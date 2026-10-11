import { useEffect, useState } from "react";
import { isInstalledApp } from "../pwa/displayMode";

// Nudges phone players to add the game to their home screen (ADR 0012).
// - Android/Chrome fires beforeinstallprompt: we hold it and offer an [ install ] button.
// - iOS Safari has no install API at all, so we spell out Share -> Add to Home Screen.
// Hidden once installed, when the browser offers no way to install, and for good once dismissed.
const DISMISS_KEY = "ttoc-install-dismissed";

function isIosSafari() {
  const ua = window.navigator.userAgent;
  const ios = /iPhone|iPad|iPod/.test(ua) || (ua.includes("Macintosh") && navigator.maxTouchPoints > 1);
  // Chrome/Firefox/Edge on iOS can't add to home screen from their own menus as reliably
  return ios && !/CriOS|FxiOS|EdgiOS/.test(ua);
}

function wasDismissed() {
  try { return localStorage.getItem(DISMISS_KEY) === "1"; } catch { return false; }
}

export default function InstallHint() {
  const [deferred, setDeferred] = useState(null);
  const [hidden, setHidden] = useState(() => isInstalledApp() || wasDismissed());

  useEffect(() => {
    const onPrompt = (e) => {
      e.preventDefault();
      setDeferred(e);
    };
    const onInstalled = () => setHidden(true);
    window.addEventListener("beforeinstallprompt", onPrompt);
    window.addEventListener("appinstalled", onInstalled);
    return () => {
      window.removeEventListener("beforeinstallprompt", onPrompt);
      window.removeEventListener("appinstalled", onInstalled);
    };
  }, []);

  const dismiss = () => {
    try { localStorage.setItem(DISMISS_KEY, "1"); } catch { /* memory only */ }
    setHidden(true);
  };

  const install = async () => {
    deferred.prompt();
    await deferred.userChoice.catch(() => null);
    setDeferred(null);
  };

  if (hidden) return null;
  if (!deferred && !isIosSafari()) return null;

  return (
    <div className="maint-banner install-hint" role="note">
      <span className="maint-tag">[ GET THE APP ]</span>
      <span className="maint-msg">
        {deferred
          ? "add it to your home screen, full screen, no store needed"
          : <>tap <b>Share</b> then <b>Add to Home Screen</b> to play full screen</>}
      </span>
      <span className="install-actions">
        {deferred && <button type="button" className="ghost" onClick={install}>install</button>}
        <button type="button" className="ghost" onClick={dismiss} aria-label="dismiss install hint">not now</button>
      </span>
    </div>
  );
}
