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
  FREE_OUTFIT_COUNT, FREE_ACCESSORY_COUNT,
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
  // Reward-unlocked premium ids (empty until the fetch lands, so premium stays locked by
  // default). Sets, so lookups in the cycler/lock markers are O(1).
  const [unlockedOutfits, setUnlockedOutfits] = useState(() => new Set());
  const [unlockedAccessories, setUnlockedAccessories] = useState(() => new Set());

  // Dressing-room idle life (phase 21): the preview periodically glances around, blinks, and
  // shifts its weight on randomized timers — a cute retro fidget. Disabled under
  // prefers-reduced-motion (a still, forward-facing character).
  const [idle, setIdle] = useState({ look: { dx: 0, dy: 0 }, blink: false, lean: 0 });
  useEffect(() => {
    const reduced =
      window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    if (reduced) return;
    const timers = [];
    const rand = (lo, hi) => lo + Math.random() * (hi - lo);
    const glance = () => {
      setIdle((s) => ({ ...s, look: { dx: rand(-1.2, 1.2), dy: rand(0, 0.6) } }));
      timers.push(setTimeout(glance, rand(1800, 3600)));
    };
    const blink = () => {
      setIdle((s) => ({ ...s, blink: true }));
      timers.push(setTimeout(() => setIdle((s) => ({ ...s, blink: false })), 140));
      timers.push(setTimeout(blink, rand(2400, 5200)));
    };
    const sway = () => {
      setIdle((s) => ({ ...s, lean: [-1, 0, 1][Math.floor(rand(0, 3))] }));
      timers.push(setTimeout(sway, rand(2400, 4200)));
    };
    timers.push(setTimeout(glance, 1200));
    timers.push(setTimeout(blink, 2000));
    timers.push(setTimeout(sway, 1600));
    return () => timers.forEach(clearTimeout);
  }, []);

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

  // Rewards the player holds unlock the premium outfit/accessory ids for saving. A saved
  // premium look still renders even without the reward (granted-then-revoked) — arrows just
  // won't cycle back onto it, and the server revalidates on save anyway.
  useEffect(() => {
    let alive = true;
    (async () => {
      const { ok, data } = await api.getRewards(token);
      if (!alive || !ok || !data) return;
      setUnlockedOutfits(new Set(data.unlockedOutfits || []));
      setUnlockedAccessories(new Set(data.unlockedAccessories || []));
    })();
    return () => { alive = false; };
  }, [token]);

  // Which ids may the arrows rest on: the free set plus anything a reward unlocked.
  const outfitAllowed = (i) => i < FREE_OUTFIT_COUNT || unlockedOutfits.has(i);
  const accessoryAllowed = (i) =>
    i === null || i < FREE_ACCESSORY_COUNT || unlockedAccessories.has(i);

  // Lock state of the currently-shown value: "locked" (premium, not owned — only reachable
  // as a saved config), "unlocked" (premium reward the player owns), or null (free).
  const lockFor = (key) => {
    if (!config) return null;
    if (key === "outfit" && config.outfit >= FREE_OUTFIT_COUNT)
      return unlockedOutfits.has(config.outfit) ? "unlocked" : "locked";
    if (key === "accessory" && config.accessory !== null && config.accessory >= FREE_ACCESSORY_COUNT)
      return unlockedAccessories.has(config.accessory) ? "unlocked" : "locked";
    return null;
  };

  const cycle = (key, dir) => {
    setConfig((c) => {
      if (!c) return c;
      if (key === "accessory") {
        const len = ACCESSORY_OPTIONS.length;
        let idx = ACCESSORY_OPTIONS.findIndex((v) => v === c.accessory);
        // Step past any locked premium accessory, landing on the next allowed one.
        for (let guard = 0; guard < len; guard++) {
          idx = (idx + dir + len) % len;
          if (accessoryAllowed(ACCESSORY_OPTIONS[idx])) break;
        }
        return { ...c, accessory: ACCESSORY_OPTIONS[idx] };
      }
      if (key === "outfit") {
        let next = c.outfit;
        for (let guard = 0; guard < OUTFIT_COUNT; guard++) {
          next = (next + dir + OUTFIT_COUNT) % OUTFIT_COUNT;
          if (outfitAllowed(next)) break;
        }
        return { ...c, outfit: next };
      }
      const counts = { base: BASE_COUNT, hair: HAIR_COUNT };
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
            <div
              className="creator-idle"
              style={{ transform: `rotate(${idle.lean * 1.6}deg) translateX(${idle.lean * 2}px)` }}
            >
              <CharacterSprite
                name={username}
                config={config}
                state="neutral"
                size={132}
                look={idle.look}
                blink={idle.blink}
              />
            </div>
          )}
        </div>
        <p className="creator-who">CHARACTER: <b>{username}</b></p>

        <div className="layers">
          {LAYERS.map((layer) => {
            const lock = lockFor(layer.key);
            return (
            <div key={layer.key} className="layer-row">
              <span className="layer-label">
                {layer.label}
                {lock === "locked" && <span className="layer-lock">[locked]</span>}
                {lock === "unlocked" && <span className="layer-reward">[reward]</span>}
              </span>
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
            );
          })}
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
