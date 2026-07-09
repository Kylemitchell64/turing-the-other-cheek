import { useEffect, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { useLobby } from "../game/LobbyContext";
import { api } from "../api/client";
import { markCreatorSkipped } from "../auth/firstUse";
import CharacterSprite from "../sprites/CharacterSprite";
import {
  configFromName, normalizeConfig,
  BASE_COUNT, HAIR_COUNT, OUTFIT_COUNT, ACCESSORY_COUNT,
} from "../sprites/config";

// Accessory cycles through "none" (null) plus each real accessory index.
const ACCESSORY_OPTIONS = [null, ...Array.from({ length: ACCESSORY_COUNT }, (_, i) => i)];

// The four layers, each a self-contained cycler over its real range (wrap-around).
const LAYERS = [
  { key: "base", label: "BASE", count: BASE_COUNT, val: (c) => c.base + 1, of: BASE_COUNT },
  { key: "hair", label: "HAIR", count: HAIR_COUNT, val: (c) => c.hair + 1, of: HAIR_COUNT },
  { key: "outfit", label: "OUTFIT", count: OUTFIT_COUNT, val: (c) => c.outfit + 1, of: OUTFIT_COUNT },
  {
    key: "accessory", label: "ACCESSORY", count: ACCESSORY_OPTIONS.length,
    val: (c) => (c.accessory === null ? "none" : `${c.accessory + 1} / ${ACCESSORY_COUNT}`), of: null,
  },
];

// Minimal, first-use character creator. Centered podium preview with the live character
// in a neutral bob; one row per layer with < > arrows; SAVE persists then continues to
// wherever the player was headed (a stashed lobby code auto-joins). Reused from Home as
// an "edit character" screen, prefilled from the saved config.
export default function CharacterCreatorPage() {
  const { token, user } = useAuth();
  const { joinLobby } = useLobby();
  const navigate = useNavigate();
  const location = useLocation();

  const username = user?.displayName || user?.unique_name || "player";
  const pendingCode = location.state?.pendingCode || null;
  const isEdit = !!location.state?.edit;

  const [config, setConfig] = useState(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState(null);

  // Prefill: the saved config if there is one, otherwise the name-hash default so the
  // creator always opens on the exact character they'd have had by default.
  useEffect(() => {
    let alive = true;
    (async () => {
      const { ok, data } = await api.getCharacter(token);
      if (!alive) return;
      setConfig(ok && data ? normalizeConfig(data, username) : configFromName(username));
    })();
    return () => { alive = false; };
  }, [token, username]);

  const cycle = (key, dir) => {
    setConfig((c) => {
      if (!c) return c;
      if (key === "accessory") {
        const idx = ACCESSORY_OPTIONS.findIndex((v) => v === c.accessory);
        const next = (idx + dir + ACCESSORY_OPTIONS.length) % ACCESSORY_OPTIONS.length;
        return { ...c, accessory: ACCESSORY_OPTIONS[next] };
      }
      const counts = { base: BASE_COUNT, hair: HAIR_COUNT, outfit: OUTFIT_COUNT };
      const n = counts[key];
      return { ...c, [key]: (c[key] + dir + n) % n };
    });
  };

  // After save/skip: resume the stashed lobby code, else land on Home.
  const resume = async () => {
    if (pendingCode) {
      try {
        await joinLobby(pendingCode);
        navigate("/lobby", { replace: true });
        return;
      } catch {
        navigate("/", { replace: true });
        return;
      }
    }
    navigate("/", { replace: true });
  };

  const onSave = async () => {
    if (!config) return;
    setBusy(true);
    setErr(null);
    const { ok } = await api.putCharacter(token, config);
    if (!ok) {
      setErr("couldn't save — try again");
      setBusy(false);
      return;
    }
    await resume();
  };

  const onSecondary = async () => {
    // Edit mode = cancel (leave the saved character as-is). First-use = skip, and
    // remember it so a returning player isn't nagged again.
    if (!isEdit) markCreatorSkipped(username);
    await resume();
  };

  return (
    <div className="screen center">
      <div className="panel creator">
        <h1 className="glow">[ {isEdit ? "EDIT CHARACTER" : "MAKE YOUR CHARACTER"} ]</h1>
        <p className="tagline">
          {isEdit ? "tweak your look." : "this is you at the podium. make it yours."}
          <span className="cursor" />
        </p>

        <div className="creator-stage">
          {config && (
            <CharacterSprite name={username} config={config} state="neutral" size={132} />
          )}
        </div>
        <p className="creator-who">CHARACTER: <b>{username}</b></p>

        <div className="layers">
          {LAYERS.map((layer) => (
            <div key={layer.key} className="layer-row">
              <span className="layer-label">{layer.label}</span>
              <div className="layer-cycler">
                <button
                  type="button"
                  className="layer-arrow"
                  onClick={() => cycle(layer.key, -1)}
                  aria-label={`previous ${layer.label}`}
                  disabled={!config}
                >
                  &lt;
                </button>
                <span className="layer-val">{config ? layer.val(config) : "…"}</span>
                <button
                  type="button"
                  className="layer-arrow"
                  onClick={() => cycle(layer.key, 1)}
                  aria-label={`next ${layer.label}`}
                  disabled={!config}
                >
                  &gt;
                </button>
              </div>
            </div>
          ))}
        </div>

        {err && <div className="error">{err}</div>}

        <button className="primary big" onClick={onSave} disabled={busy || !config}>
          {busy ? "..." : "SAVE"}
        </button>
        <button type="button" className="link" onClick={onSecondary} disabled={busy}>
          {isEdit ? "cancel" : "skip for now"}
        </button>
      </div>
    </div>
  );
}
