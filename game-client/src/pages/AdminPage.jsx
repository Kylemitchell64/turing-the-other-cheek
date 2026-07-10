import { useCallback, useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { api } from "../api/client";

// Grantable rewards, matching the server's RewardKinds gate (premium outfits 6..9,
// accessories 3..5, and the one-shot cheat card).
const GRANTABLE = [
  ...[6, 7, 8, 9].map((i) => ({ kind: `outfit:${i}`, label: `outfit ${i}` })),
  ...[3, 4, 5].map((i) => ({ kind: `accessory:${i}`, label: `accessory ${i}` })),
  { kind: "cheat_card", label: "cheat card" },
];

const PAGE_SIZE = 12;

function fmtDate(iso) {
  if (!iso) return "—";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "—";
  return d.toLocaleDateString(undefined, { month: "short", day: "numeric", year: "2-digit" });
}

function fmtBytes(n) {
  if (!n) return "0 B";
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

// A terminal 404 for signed-in non-admins who guess the URL.
function NotAdmin({ onHome }) {
  return (
    <div className="screen center">
      <div className="panel admin-404">
        <h1 className="glow">[ 404 ]</h1>
        <p className="tagline">no such sector. this terminal is not yours.<span className="cursor" /></p>
        <button className="ghost" onClick={onHome}>back to safety</button>
      </div>
    </div>
  );
}

export default function AdminPage() {
  const { token, user } = useAuth();
  const navigate = useNavigate();
  const isAdmin = user?.isAdmin === "true";

  const [overview, setOverview] = useState(null);
  const [freetier, setFreetier] = useState(null);
  const [timeline, setTimeline] = useState(null);

  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const [usersData, setUsersData] = useState(null);
  const [notice, setNotice] = useState(null);

  // Ops: current maintenance switch (seeded once from the public probe), and the modal
  // machine driving the restart + wipe confirmations.
  const [maintOn, setMaintOn] = useState(false);
  const [maintMsg, setMaintMsg] = useState("");
  const [modal, setModal] = useState(null); // null | "restart" | "wipe"

  // Live-ish analytics: pull the read endpoints on mount and refresh the volatile ones.
  useEffect(() => {
    if (!isAdmin) return;
    let alive = true;
    const load = async () => {
      const [ov, ft, tl] = await Promise.all([
        api.adminOverview(token),
        api.adminFreeTier(token),
        api.adminTimeline(token),
      ]);
      if (!alive) return;
      if (ov.ok) setOverview(ov.data);
      if (ft.ok) setFreetier(ft.data);
      if (tl.ok) setTimeline(tl.data);
    };
    load();
    const id = setInterval(load, 15000);
    return () => { alive = false; clearInterval(id); };
  }, [token, isAdmin]);

  const loadUsers = useCallback(async () => {
    const { ok, data } = await api.adminUsers(token, { search, page, pageSize: PAGE_SIZE });
    if (ok) setUsersData(data);
  }, [token, search, page]);

  // Debounced search / paging.
  useEffect(() => {
    if (!isAdmin) return;
    const id = setTimeout(loadUsers, 250);
    return () => clearTimeout(id);
  }, [loadUsers, isAdmin]);

  const grant = async (id, kind) => {
    if (!kind) return;
    const { ok } = await api.adminGrant(token, id, kind);
    setNotice(ok ? `granted ${kind}` : `grant failed`);
    await loadUsers();
  };

  const revoke = async (id, kind) => {
    const { ok } = await api.adminRevoke(token, id, kind);
    setNotice(ok ? `revoked ${kind}` : `revoke failed`);
    await loadUsers();
  };

  // Seed the maintenance form from the live status once (later edits are the operator's).
  useEffect(() => {
    if (!isAdmin) return;
    let alive = true;
    (async () => {
      const { ok, data } = await api.getStatus();
      if (alive && ok && data) { setMaintOn(!!data.maintenance); setMaintMsg(data.message || ""); }
    })();
    return () => { alive = false; };
  }, [isAdmin]);

  const applyMaintenance = async (on) => {
    const { ok, data } = await api.adminMaintenance(token, on, maintMsg);
    if (ok) { setMaintOn(!!data.maintenance); setNotice(data.maintenance ? "maintenance ON" : "maintenance off"); }
    else setNotice("couldn't change maintenance");
  };

  const doRestart = async () => {
    setModal(null);
    const { ok } = await api.adminRestart(token);
    setNotice(ok ? "restart requested — the server is coming back up…" : "restart failed");
  };

  const doWipe = async () => {
    setModal(null);
    const { ok, data } = await api.adminWipe(token, "WIPE EVERYTHING");
    if (ok) {
      setNotice(`wiped — ${data.accountsRemoved} accounts removed, ${data.adminsKept} kept`);
      await loadUsers();
    } else {
      setNotice("wipe failed");
    }
  };

  if (!isAdmin) return <NotAdmin onHome={() => navigate("/")} />;

  return (
    <div className="screen admin">
      <div className="topbar">
        <span className="who">[ ADMIN CONSOLE ]</span>
        <button className="ghost" onClick={() => navigate("/")}>exit</button>
      </div>

      {notice && <div className="admin-notice" onClick={() => setNotice(null)}>{notice}</div>}

      <Overview overview={overview} />
      <FreeTier freetier={freetier} />
      <Timeline timeline={timeline} />
      <Users
        usersData={usersData}
        search={search}
        page={page}
        onSearch={(v) => { setSearch(v); setPage(1); }}
        onPage={setPage}
        onGrant={grant}
        onRevoke={revoke}
      />

      <Ops
        maintOn={maintOn}
        maintMsg={maintMsg}
        onMsg={setMaintMsg}
        onApply={applyMaintenance}
        onRestart={() => setModal("restart")}
      />

      <DangerZone onWipe={() => setModal("wipe")} />

      {modal === "restart" && (
        <RestartModal onConfirm={doRestart} onCancel={() => setModal(null)} />
      )}
      {modal === "wipe" && (
        <WipeModal onConfirm={doWipe} onCancel={() => setModal(null)} />
      )}
    </div>
  );
}

function Overview({ overview }) {
  const tiles = overview
    ? [
        ["total users", overview.totalUsers],
        ["guests", overview.guests],
        ["registered", overview.registered],
        ["oauth", overview.oauth],
        ["games total", overview.gamesTotal],
        ["games today", overview.gamesToday],
        ["games 7d", overview.games7d],
        ["active lobbies", overview.activeLobbies],
      ]
    : [];

  return (
    <section className="admin-section">
      <div className="crt-head">[ OVERVIEW ]</div>
      {!overview ? (
        <p className="admin-loading">loading…</p>
      ) : (
        <>
          <div className="admin-tiles">
            {tiles.map(([lab, num]) => (
              <div className="stat-card" key={lab}>
                <div className="stat-num">{num}</div>
                <div className="stat-lab">{lab}</div>
              </div>
            ))}
          </div>
          {overview.aiProviders?.length > 0 && (
            <div className="admin-ai-tiles">
              {overview.aiProviders.map((p) => (
                <div
                  className={`ai-tile ${p.breakerOpen ? "brk" : ""} ${p.exhaustedForDay ? "exh" : ""}`}
                  key={p.provider}
                >
                  <div className="ai-tile-name">{p.provider}</div>
                  <div className="ai-tile-num">{p.requestsToday}</div>
                  <div className="ai-tile-sub">req today</div>
                  <div className="ai-tile-state">
                    {p.breakerOpen ? "breaker open" : p.exhaustedForDay ? "quota spent" : "healthy"}
                    {" · "}{p.failoverHops} hops
                  </div>
                </div>
              ))}
            </div>
          )}
        </>
      )}
    </section>
  );
}

function FreeTier({ freetier }) {
  return (
    <section className="admin-section">
      <div className="crt-head">[ FREE TIER ]</div>
      {!freetier ? (
        <p className="admin-loading">loading…</p>
      ) : (
        <div className="ft-wrap">
          <div className="ft-bars">
            {freetier.resources.map((r) => (
              <div className="ft-row" key={r.key}>
                <div className="ft-label">
                  <span>{r.label}</span>
                  <span className="ft-nums">{r.used} / {r.limit} {r.unit}</span>
                </div>
                <div className="ft-track">
                  <div
                    className={`ft-fill ${r.percent >= 90 ? "hot" : r.percent >= 70 ? "warm" : ""}`}
                    style={{ width: `${Math.min(100, r.percent)}%` }}
                  />
                </div>
              </div>
            ))}
          </div>
          <div className="ft-gauge">
            <div className={`ft-gauge-num ${freetier.average >= 90 ? "hot" : freetier.average >= 70 ? "warm" : ""}`}>
              {Math.round(freetier.average)}%
            </div>
            <div className="ft-gauge-lab">avg tier usage</div>
          </div>
        </div>
      )}
    </section>
  );
}

function Timeline({ timeline }) {
  const days = timeline?.days || [];
  const max = Math.max(1, ...days.map((d) => d.count));
  const W = 620;
  const H = 120;
  const gap = 3;
  const bw = days.length ? (W - gap * (days.length - 1)) / days.length : 0;

  return (
    <section className="admin-section">
      <div className="crt-head">[ TIMELINE · 30d games/day ]</div>
      {!timeline ? (
        <p className="admin-loading">loading…</p>
      ) : (
        <div className="tl-wrap">
          <svg viewBox={`0 0 ${W} ${H}`} className="tl-svg" preserveAspectRatio="none" role="img"
            aria-label="games per day, last 30 days">
            {days.map((d, i) => {
              const h = (d.count / max) * (H - 16);
              return (
                <g key={d.date}>
                  <rect
                    x={i * (bw + gap)}
                    y={H - h - 2}
                    width={bw}
                    height={Math.max(1, h)}
                    rx="1"
                    className={d.count > 0 ? "tl-bar" : "tl-bar zero"}
                  >
                    <title>{d.date}: {d.count}</title>
                  </rect>
                </g>
              );
            })}
          </svg>
          <div className="tl-axis">
            <span>{days[0]?.date?.slice(5)}</span>
            <span>peak {max}</span>
            <span>{days[days.length - 1]?.date?.slice(5)}</span>
          </div>
        </div>
      )}
    </section>
  );
}

function Users({ usersData, search, page, onSearch, onPage, onGrant, onRevoke }) {
  const users = usersData?.users || [];
  const total = usersData?.total || 0;
  const maxUsage = usersData?.maxDataUsage || 0;
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  return (
    <section className="admin-section">
      <div className="crt-head">[ USERS ]</div>
      <input
        className="admin-search"
        type="text"
        placeholder="search name…"
        value={search}
        onChange={(e) => onSearch(e.target.value)}
      />

      <div className="admin-users">
        <div className="au-row au-head">
          <span className="au-name">name</span>
          <span className="au-tier">tier</span>
          <span className="au-seen">last seen</span>
          <span className="au-games">games</span>
          <span className="au-data">data</span>
          <span className="au-rewards">rewards</span>
          <span className="au-grant">grant</span>
        </div>

        {users.map((u) => (
          <UserRow key={u.id} u={u} maxUsage={maxUsage} onGrant={onGrant} onRevoke={onRevoke} />
        ))}
        {users.length === 0 && <p className="admin-loading">no users</p>}
      </div>

      <div className="admin-pager">
        <button className="ghost" disabled={page <= 1} onClick={() => onPage(page - 1)}>prev</button>
        <span className="admin-pageinfo">page {page} / {pages} · {total} users</span>
        <button className="ghost" disabled={page >= pages} onClick={() => onPage(page + 1)}>next</button>
      </div>
    </section>
  );
}

function UserRow({ u, maxUsage, onGrant, onRevoke }) {
  const [kind, setKind] = useState(GRANTABLE[0].kind);
  const dataPct = maxUsage > 0 ? (u.dataUsage / maxUsage) * 100 : 0;
  const r = u.rewards || { outfits: [], accessories: [], cheatCards: 0 };
  const chips = [
    ...r.outfits.map((i) => ({ kind: `outfit:${i}`, label: `o${i}` })),
    ...r.accessories.map((i) => ({ kind: `accessory:${i}`, label: `a${i}` })),
    ...(r.cheatCards > 0 ? [{ kind: "cheat_card", label: `cc×${r.cheatCards}` }] : []),
  ];

  return (
    <div className="au-row">
      <span className="au-name" title={u.username}>{u.displayName}</span>
      <span className="au-tier"><span className={`tier-badge tier-${u.tier}`}>{u.tier}</span></span>
      <span className="au-seen">{fmtDate(u.lastSeen)}</span>
      <span className="au-games">{u.gamesPlayed}</span>
      <span className="au-data">
        <span className="au-data-bar"><span style={{ width: `${dataPct}%` }} /></span>
        <span className="au-data-num">{fmtBytes(u.dataUsage)}</span>
      </span>
      <span className="au-rewards">
        {chips.length === 0 && <span className="au-none">—</span>}
        {chips.map((c) => (
          <button key={c.kind + c.label} className="reward-chip" title={`revoke ${c.kind}`}
            onClick={() => onRevoke(u.id, c.kind)}>
            {c.label} <span className="rc-x">×</span>
          </button>
        ))}
      </span>
      <span className="au-grant">
        <select value={kind} onChange={(e) => setKind(e.target.value)}>
          {GRANTABLE.map((g) => <option key={g.kind} value={g.kind}>{g.label}</option>)}
        </select>
        <button className="ghost au-grant-btn" onClick={() => onGrant(u.id, kind)}>+</button>
      </span>
    </div>
  );
}

function Ops({ maintOn, maintMsg, onMsg, onApply, onRestart }) {
  return (
    <section className="admin-section">
      <div className="crt-head">[ OPS ]</div>
      <div className="ops-box">
        <div className="ops-row">
          <span className={`ops-state ${maintOn ? "on" : ""}`}>
            maintenance {maintOn ? "ON — new games paused" : "off"}
          </span>
        </div>
        <textarea
          className="sample-input ops-msg"
          placeholder="operator message shown to players while paused…"
          value={maintMsg}
          onChange={(e) => onMsg(e.target.value)}
          maxLength={200}
        />
        <div className="ops-actions">
          {maintOn ? (
            <button className="ghost" onClick={() => onApply(false)}>resume games</button>
          ) : (
            <button className="danger-btn ops-pause" onClick={() => onApply(true)}>pause for maintenance</button>
          )}
          {maintOn && (
            <button className="ghost" onClick={() => onApply(true)}>update message</button>
          )}
          <button className="ghost ops-restart" onClick={onRestart}>restart server</button>
        </div>
      </div>
    </section>
  );
}

function DangerZone({ onWipe }) {
  return (
    <section className="admin-section danger-zone">
      <div className="crt-head danger-head">[ DANGER ZONE ]</div>
      <div className="danger-box">
        <p className="danger-copy">
          wipe every game and all non-admin accounts. permanent. admin logins survive.
        </p>
        <button className="danger-btn" onClick={onWipe}>wipe everything…</button>
      </div>
    </section>
  );
}

function RestartModal({ onConfirm, onCancel }) {
  return (
    <div className="modal-backdrop" onClick={onCancel}>
      <div className="modal terminal" onClick={(e) => e.stopPropagation()}>
        <h2 className="modal-head">[ RESTART SERVER ]</h2>
        <p className="modal-copy">
          the server process will exit and relaunch. active lobbies drop and the maintenance
          flag clears. a running game in progress will be lost.
        </p>
        <button className="danger-btn" onClick={onConfirm}>restart now</button>
        <button className="link" onClick={onCancel}>cancel</button>
      </div>
    </div>
  );
}

const WIPE_PHRASE = "WIPE EVERYTHING";

// Three escalating gates; the last requires typing the exact phrase.
function WipeModal({ onConfirm, onCancel }) {
  const [step, setStep] = useState(1);
  const [phrase, setPhrase] = useState("");

  return (
    <div className="modal-backdrop" onClick={onCancel}>
      <div className="modal terminal wipe-modal" onClick={(e) => e.stopPropagation()}>
        <h2 className="modal-head danger-head">[ WIPE — STEP {step} / 3 ]</h2>

        {step === 1 && (
          <>
            <p className="modal-copy">
              this deletes ALL game history and EVERY non-admin account, along with their
              characters, samples, stats, and rewards. there is no undo.
            </p>
            <button className="danger-btn" onClick={() => setStep(2)}>I understand — continue</button>
            <button className="link" onClick={onCancel}>cancel</button>
          </>
        )}

        {step === 2 && (
          <>
            <p className="modal-copy">
              last chance to back out. only admin logins will remain afterward. are you
              absolutely sure?
            </p>
            <button className="danger-btn" onClick={() => setStep(3)}>yes, I'm sure</button>
            <button className="link" onClick={onCancel}>cancel</button>
          </>
        )}

        {step === 3 && (
          <>
            <p className="modal-copy">
              type <b className="ai-name">{WIPE_PHRASE}</b> to confirm.
            </p>
            <input
              type="text"
              value={phrase}
              onChange={(e) => setPhrase(e.target.value)}
              placeholder={WIPE_PHRASE}
              autoFocus
            />
            <button
              className="danger-btn"
              disabled={phrase !== WIPE_PHRASE}
              onClick={onConfirm}
            >
              wipe everything
            </button>
            <button className="link" onClick={onCancel}>cancel</button>
          </>
        )}
      </div>
    </div>
  );
}
