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
  const [filter, setFilter] = useState("all");
  const [sort, setSort] = useState("lastSeen");
  const [page, setPage] = useState(1);
  const [usersData, setUsersData] = useState(null);
  const [notice, setNotice] = useState(null);

  // The user whose profile drawer is open (id), and the fetched synopsis for it.
  const [profileId, setProfileId] = useState(null);
  const [profile, setProfile] = useState(null);
  const [pendingDelete, setPendingDelete] = useState(null); // the user object awaiting delete confirm

  // Ops: current maintenance switch (seeded once from the public probe), and the modal
  // machine driving the restart + wipe + purge confirmations.
  const [maintOn, setMaintOn] = useState(false);
  const [maintMsg, setMaintMsg] = useState("");
  const [modal, setModal] = useState(null); // null | "restart" | "wipe" | "purge"

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
    const { ok, data } = await api.adminUsers(token, { search, page, pageSize: PAGE_SIZE, filter, sort });
    if (ok) setUsersData(data);
  }, [token, search, page, filter, sort]);

  // Debounced search / paging.
  useEffect(() => {
    if (!isAdmin) return;
    const id = setTimeout(loadUsers, 250);
    return () => clearTimeout(id);
  }, [loadUsers, isAdmin]);

  // Fetch (or refresh) the open profile drawer.
  const loadProfile = useCallback(async (id) => {
    if (!id) return;
    const { ok, data } = await api.adminUserProfile(token, id);
    if (ok) setProfile(data);
  }, [token]);

  useEffect(() => {
    if (profileId) loadProfile(profileId);
    else setProfile(null);
  }, [profileId, loadProfile]);

  const openProfile = (id) => { setProfile(null); setProfileId(id); };
  const closeProfile = () => { setProfileId(null); setProfile(null); };

  const grant = async (id, kind) => {
    if (!kind) return;
    const { ok } = await api.adminGrant(token, id, kind);
    setNotice(ok ? `granted ${labelForKind(kind)} — it now shows on their rewards.` : "couldn't grant that reward.");
    await Promise.all([loadUsers(), loadProfile(id)]);
  };

  const revoke = async (id, kind) => {
    const { ok } = await api.adminRevoke(token, id, kind);
    setNotice(ok ? `revoked ${labelForKind(kind)}.` : "couldn't revoke that reward.");
    await Promise.all([loadUsers(), loadProfile(id)]);
  };

  const deleteUser = async (u) => {
    setPendingDelete(null);
    const { ok, data } = await api.adminDeleteUser(token, u.id);
    if (ok) {
      setNotice(`deleted ${data.displayName || u.displayName} and all their data.`);
      closeProfile();
      await loadUsers();
    } else {
      setNotice(data?.error ? `couldn't delete: ${data.error}` : "couldn't delete that account.");
    }
  };

  const purgeGuests = async () => {
    setModal(null);
    const { ok, data } = await api.adminPurgeNonOauth(token, "DELETE GUESTS");
    if (ok) {
      setNotice(`purged ${data.deleted} non-oauth account${data.deleted === 1 ? "" : "s"} (guests + password logins).`);
      await loadUsers();
    } else {
      setNotice("couldn't purge non-oauth accounts.");
    }
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

      {notice && (
        <div className="admin-notice" role="status">
          <span className="admin-notice-text">{notice}</span>
          <button className="admin-notice-x" onClick={() => setNotice(null)}>dismiss ✕</button>
        </div>
      )}

      <Overview overview={overview} />
      <FreeTier freetier={freetier} />
      <Timeline timeline={timeline} />
      <Users
        usersData={usersData}
        search={search}
        filter={filter}
        sort={sort}
        page={page}
        onSearch={(v) => { setSearch(v); setPage(1); }}
        onFilter={(v) => { setFilter(v); setPage(1); }}
        onSort={(v) => { setSort(v); setPage(1); }}
        onPage={setPage}
        onOpen={openProfile}
      />

      <SelfCheck token={token} />
      <Cleanup token={token} onDone={loadUsers} />
      <Cheats token={token} />

      <Ops
        maintOn={maintOn}
        maintMsg={maintMsg}
        onMsg={setMaintMsg}
        onApply={applyMaintenance}
        onRestart={() => setModal("restart")}
      />

      <DangerZone onWipe={() => setModal("wipe")} onPurge={() => setModal("purge")} />

      {profileId && (
        <UserProfile
          profile={profile}
          onClose={closeProfile}
          onGrant={grant}
          onRevoke={revoke}
          onDelete={(u) => setPendingDelete(u)}
        />
      )}
      {pendingDelete && (
        <DeleteUserModal user={pendingDelete} onConfirm={() => deleteUser(pendingDelete)} onCancel={() => setPendingDelete(null)} />
      )}
      {modal === "restart" && (
        <RestartModal onConfirm={doRestart} onCancel={() => setModal(null)} />
      )}
      {modal === "wipe" && (
        <WipeModal onConfirm={doWipe} onCancel={() => setModal(null)} />
      )}
      {modal === "purge" && (
        <PurgeModal onConfirm={purgeGuests} onCancel={() => setModal(null)} />
      )}
    </div>
  );
}

// Human labels for reward kinds, shared by the grant control, chips, and notices.
function labelForKind(kind) {
  if (kind === "cheat_card") return "cheat card";
  const [type, id] = kind.split(":");
  if (type === "outfit") return `outfit ${id}`;
  if (type === "accessory") return `accessory ${id}`;
  return kind;
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
                {r.key === "render" && (
                  <p className="ft-note">
                    render hours reset on the 1st — 24/7 uptime burns ~744 of 750 hrs/mo. that's normal, not a leak.
                  </p>
                )}
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
      <div className="crt-head">[ TIMELINE · 30d ]</div>
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

const USER_FILTERS = [
  { key: "all", label: "all" },
  { key: "inactive", label: "inactive 30d+" },
  { key: "guests", label: "guests" },
  { key: "oauth", label: "oauth" },
  { key: "safe-delete", label: "safe to delete" },
];
const USER_SORTS = [
  { key: "lastSeen", label: "last seen" },
  { key: "storage", label: "storage" },
  { key: "games", label: "games" },
  { key: "name", label: "name" },
];

function Users({ usersData, search, filter, sort, page, onSearch, onFilter, onSort, onPage, onOpen }) {
  const users = usersData?.users || [];
  const total = usersData?.total || 0;
  const maxUsage = usersData?.maxDataUsage || 0;
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  return (
    <section className="admin-section">
      <div className="crt-head">[ USERS ]</div>
      <p className="admin-hint">tap a user to see their full profile, grant/revoke rewards, or delete the account.</p>
      <input
        className="admin-search"
        type="text"
        placeholder="search name…"
        value={search}
        onChange={(e) => onSearch(e.target.value)}
      />
      <div className="admin-filters">
        <div className="segmented">
          {USER_FILTERS.map((f) => (
            <button key={f.key} className={`seg ${filter === f.key ? "on" : ""}`} onClick={() => onFilter(f.key)} aria-pressed={filter === f.key}>
              {f.label}
            </button>
          ))}
        </div>
        <label className="admin-sort">
          sort
          <select value={sort} onChange={(e) => onSort(e.target.value)}>
            {USER_SORTS.map((o) => <option key={o.key} value={o.key}>{o.label}</option>)}
          </select>
        </label>
      </div>
      {filter === "safe-delete" && (
        <p className="admin-hint">// guests that never played, hold no samples and haven't been seen in 24h — probes and abandoned quick-plays. deleting them loses nothing; CLEANUP below does it in one go.</p>
      )}

      <div className="admin-users">
        <div className="au-row au-head">
          <span className="au-name">name</span>
          <span className="au-tier">tier</span>
          <span className="au-seen">last seen</span>
          <span className="au-games">games</span>
          <span className="au-data">data</span>
          <span className="au-rewards">rewards</span>
        </div>

        {users.map((u) => (
          <UserRow key={u.id} u={u} maxUsage={maxUsage} onOpen={onOpen} />
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

// One reward summary → readable chips (read-only in the row; managed in the profile drawer).
function rewardChips(r) {
  const rw = r || { outfits: [], accessories: [], cheatCards: 0 };
  return [
    ...rw.outfits.map((i) => ({ kind: `outfit:${i}`, label: `outfit ${i}` })),
    ...rw.accessories.map((i) => ({ kind: `accessory:${i}`, label: `accessory ${i}` })),
    ...(rw.cheatCards > 0 ? [{ kind: "cheat_card", label: `cheat ×${rw.cheatCards}` }] : []),
  ];
}

function UserRow({ u, maxUsage, onOpen }) {
  const dataPct = maxUsage > 0 ? (u.dataUsage / maxUsage) * 100 : 0;
  const chips = rewardChips(u.rewards);

  return (
    <button className="au-row au-row-btn" onClick={() => onOpen(u.id)}>
      <span className="au-name" title={u.username}>{u.displayName}</span>
      <span className="au-tier"><span className={`tier-badge tier-${u.tier}`}>{u.tier}</span></span>
      <span className="au-seen">{fmtDate(u.lastSeen)}</span>
      <span className="au-games">{u.gamesPlayed}</span>
      <span className="au-data" title={`${u.samples ?? 0} writing sample${u.samples === 1 ? "" : "s"}`}>
        <span className="au-data-bar"><span style={{ width: `${dataPct}%` }} /></span>
        <span className="au-data-num">{fmtBytes(u.dataUsage)}{u.safeDelete ? " · safe" : ""}</span>
      </span>
      <span className="au-rewards">
        {chips.length === 0
          ? <span className="au-none">no rewards</span>
          : chips.map((c) => <span key={c.kind + c.label} className="reward-chip static">{c.label}</span>)}
      </span>
    </button>
  );
}

// The per-user profile drawer: everything about one account, plus the grant/revoke and
// delete controls (which used to be a mystery "+" on the row).
function UserProfile({ profile, onClose, onGrant, onRevoke, onDelete }) {
  const [kind, setKind] = useState(GRANTABLE[0].kind);

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal terminal user-profile" onClick={(e) => e.stopPropagation()}>
        {!profile ? (
          <p className="admin-loading">loading profile…</p>
        ) : (
          <>
            <div className="up-head">
              <h2 className="modal-head">{profile.displayName}</h2>
              <span className={`tier-badge tier-${profile.tier}`}>{profile.tier}</span>
            </div>
            <p className="up-sub">
              @{profile.username} · {profile.provider}
              {profile.email ? ` · ${profile.email}` : ""}
            </p>

            <div className="up-grid">
              <ProfStat label="last seen" value={fmtDate(profile.lastSeen)} />
              <ProfStat label="games played" value={profile.gamesPlayed} />
              <ProfStat label="detector wins" value={profile.detectorWins} />
              <ProfStat label="times fooled" value={profile.timesFooled} />
              <ProfStat label="read by AI" value={profile.timesReadByAi} />
              <ProfStat label="AI escapes seen" value={profile.aiSurvivalGamesWitnessed} />
              <ProfStat label="writing samples" value={`${profile.sampleCount} · ${fmtBytes(profile.sampleChars)}`} />
              <ProfStat label="character saved" value={profile.hasCharacter ? "yes" : "no"} />
            </div>

            <div className="up-block">
              <div className="up-block-lab">crews</div>
              {profile.crews?.length ? (
                <div className="up-crews">
                  {profile.crews.map((c) => (
                    <span key={c.joinCode} className="up-crew">
                      {c.name}{c.isOwner ? " (owner)" : ""}
                    </span>
                  ))}
                </div>
              ) : <span className="au-none">none</span>}
            </div>

            <div className="up-block">
              <div className="up-block-lab">rewards</div>
              <div className="up-rewards">
                {rewardChips(profile.rewards).length === 0 && <span className="au-none">none</span>}
                {rewardChips(profile.rewards).map((c) => (
                  <button key={c.kind + c.label} className="reward-chip" title={`revoke ${c.label}`}
                    onClick={() => onRevoke(profile.id, c.kind)}>
                    {c.label} <span className="rc-x">✕</span>
                  </button>
                ))}
              </div>
              <div className="up-grant">
                <select value={kind} onChange={(e) => setKind(e.target.value)}>
                  {GRANTABLE.map((g) => <option key={g.kind} value={g.kind}>{g.label}</option>)}
                </select>
                <button className="ghost" onClick={() => onGrant(profile.id, kind)}>grant reward</button>
              </div>
            </div>

            <div className="up-actions">
              <button className="link" onClick={onClose}>close</button>
              {profile.isAdmin ? (
                <span className="up-protected">admin — protected, can't be deleted</span>
              ) : (
                <button className="danger-btn" onClick={() => onDelete(profile)}>delete account…</button>
              )}
            </div>
          </>
        )}
      </div>
    </div>
  );
}

function ProfStat({ label, value }) {
  return (
    <div className="prof-stat">
      <div className="prof-stat-num">{value}</div>
      <div className="prof-stat-lab">{label}</div>
    </div>
  );
}

function DeleteUserModal({ user, onConfirm, onCancel }) {
  return (
    <div className="modal-backdrop" onClick={onCancel}>
      <div className="modal terminal" onClick={(e) => e.stopPropagation()}>
        <h2 className="modal-head danger-head">[ DELETE ACCOUNT ]</h2>
        <p className="modal-copy">
          permanently delete <b className="ai-name">{user.displayName}</b> and everything tied to
          the account — writing samples, stats, rewards, and crew memberships. there is no undo.
        </p>
        <button className="danger-btn" onClick={onConfirm}>delete {user.displayName}</button>
        <button className="link" onClick={onCancel}>cancel</button>
      </div>
    </div>
  );
}

const PURGE_PHRASE = "DELETE GUESTS";

// Deletes every non-oauth account (guests + legacy password logins). Lighter than the full
// wipe (two steps) but still gated by a typed phrase.
function PurgeModal({ onConfirm, onCancel }) {
  const [step, setStep] = useState(1);
  const [phrase, setPhrase] = useState("");

  return (
    <div className="modal-backdrop" onClick={onCancel}>
      <div className="modal terminal" onClick={(e) => e.stopPropagation()}>
        <h2 className="modal-head danger-head">[ DELETE NON-OAUTH USERS ]</h2>
        {step === 1 ? (
          <>
            <p className="modal-copy">
              this deletes every account WITHOUT a Google/GitHub login — all guests and any
              legacy password accounts — along with their samples, stats, and rewards. oauth
              logins (including admins) are untouched. no undo.
            </p>
            <button className="danger-btn" onClick={() => setStep(2)}>continue</button>
            <button className="link" onClick={onCancel}>cancel</button>
          </>
        ) : (
          <>
            <p className="modal-copy">type <b className="ai-name">{PURGE_PHRASE}</b> to confirm.</p>
            <input
              type="text"
              value={phrase}
              onChange={(e) => setPhrase(e.target.value)}
              placeholder={PURGE_PHRASE}
              autoFocus
            />
            <button className="danger-btn" disabled={phrase !== PURGE_PHRASE} onClick={onConfirm}>
              delete non-oauth users
            </button>
            <button className="link" onClick={onCancel}>cancel</button>
          </>
        )}
      </div>
    </div>
  );
}

// ---- phase 30: self-check ----
// Runs the whole chain server-side (db, migrations, config, a real AI ping, a synthetic
// solo game, lobby store, free-tier headroom) and streams each step's verdict here.
function SelfCheck({ token }) {
  const [run, setRun] = useState(null);
  const [err, setErr] = useState(null);

  const poll = useCallback(async () => {
    const { ok, data } = await api.adminSelfCheck(token);
    if (ok) setRun(data);
    return ok && data;
  }, [token]);

  useEffect(() => { poll(); }, [poll]);

  useEffect(() => {
    if (!run?.running) return;
    const id = setInterval(poll, 1000);
    return () => clearInterval(id);
  }, [run?.running, poll]);

  const start = async () => {
    setErr(null);
    const { ok, data, status } = await api.adminSelfCheckStart(token);
    if (ok) setRun(data);
    else setErr(status === 409 ? "a check is already running" : "couldn't start the self-check");
  };

  const steps = run?.steps || [];
  const worst = steps.some((s) => s.status === "fail") ? "fail" : steps.some((s) => s.status === "warn") ? "warn" : steps.length ? "pass" : null;

  return (
    <section className="admin-section">
      <div className="crt-head">[ SELF-CHECK ]</div>
      <p className="admin-hint">exercises the real thing: database, schema, config, one AI call, a synthetic solo game on fast clocks (bots, the AI, an accusation and a fake-out), the lobby store, and every free-tier cap.</p>
      <div className="ops-actions">
        <button className="primary sc-run" onClick={start} disabled={!!run?.running}>
          {run?.running ? "running…" : "run self-check"}
        </button>
        {run?.finishedUtc && !run.running && (
          <span className={`sc-verdict sc-${worst}`}>
            {worst === "pass" ? "all clear" : worst === "warn" ? "warnings" : "problems"} · {fmtDate(run.finishedUtc)}
          </span>
        )}
        {err && <span className="error inline">{err}</span>}
      </div>
      {steps.length > 0 && (
        <ul className="sc-steps">
          {steps.map((s) => (
            <li key={s.key} className={`sc-step sc-${s.status}`}>
              <span className="sc-dot" aria-hidden="true" />
              <span className="sc-label">{s.label}</span>
              <span className="sc-status">{s.status}{s.ms ? ` · ${s.ms < 1000 ? `${s.ms}ms` : `${(s.ms / 1000).toFixed(1)}s`}` : ""}</span>
              {s.detail && <span className="sc-detail">{s.detail}</span>}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

// ---- phase 30: cleanup ----
function Cleanup({ token, onDone }) {
  const [preview, setPreview] = useState(null);
  const [result, setResult] = useState(null);
  const [busy, setBusy] = useState(false);
  const [phrase, setPhrase] = useState("");

  const dryRun = async () => {
    setBusy(true); setResult(null);
    const { ok, data } = await api.adminCleanup(token, { dryRun: true });
    setPreview(ok ? data : null);
    setBusy(false);
  };
  const go = async () => {
    setBusy(true);
    const { ok, data } = await api.adminCleanup(token, { dryRun: false, confirm: phrase.trim() });
    setResult(ok ? data : { error: data?.error || "cleanup failed" });
    setPreview(null); setPhrase("");
    setBusy(false);
    if (ok) onDone?.();
  };
  const nothing = preview && !preview.safeDelete && !preview.staleGuests && !preview.oldRewards && !preview.deadLobbies;

  return (
    <section className="admin-section">
      <div className="crt-head">[ CLEANUP ]</div>
      <p className="admin-hint">freshen the app without touching anything a real player would miss: probe/abandoned guest accounts, guests past the 30-day retention rule, consumed rewards older than 90 days, and dead in-memory lobbies. always preview first.</p>
      <div className="ops-actions">
        <button className="ghost" onClick={dryRun} disabled={busy}>preview cleanup</button>
      </div>
      {preview && (
        <div className="cl-box">
          <div className="cl-grid">
            <span>safe-to-delete accounts</span><b>{preview.safeDelete}</b>
            <span>stale guests (30d+)</span><b>{preview.staleGuests}</b>
            <span>old consumed rewards</span><b>{preview.oldRewards}</b>
            <span>dead lobbies in memory</span><b>{preview.deadLobbies}</b>
          </div>
          {nothing ? (
            <p className="soon">// nothing to clean. the app is fresh.</p>
          ) : (
            <div className="cl-confirm">
              <input className="sample-input" placeholder='type CLEANUP to confirm' value={phrase} onChange={(e) => setPhrase(e.target.value)} />
              <button className="danger-btn" onClick={go} disabled={busy || phrase.trim() !== "CLEANUP"}>run cleanup</button>
            </div>
          )}
        </div>
      )}
      {result && (
        <p className="admin-hint">
          {result.error ? result.error : `done — removed ${result.removedUsers} account${result.removedUsers === 1 ? "" : "s"}, ${result.oldRewards} reward row${result.oldRewards === 1 ? "" : "s"}, ${result.deadLobbies} lobb${result.deadLobbies === 1 ? "y" : "ies"}.`}
        </p>
      )}
    </section>
  );
}

// ---- phase 30: operator cheats ----
function Cheats({ token }) {
  const [state, setState] = useState(null);
  useEffect(() => {
    let alive = true;
    api.adminCheats(token).then(({ ok, data }) => { if (alive && ok) setState(data); });
    return () => { alive = false; };
  }, [token]);
  const toggle = async (key) => {
    const { ok, data } = await api.adminSetCheats(token, { [key]: !state?.[key] });
    if (ok) setState(data);
  };
  const Row = ({ k, label, blurb }) => (
    <button type="button" className={`cheat-row ${state?.[k] ? "on" : ""}`} onClick={() => toggle(k)} aria-pressed={!!state?.[k]} disabled={!state}>
      <span className="cheat-switch" aria-hidden="true">{state?.[k] ? "ON" : "off"}</span>
      <span className="cheat-text"><b>{label}</b><span>{blurb}</span></span>
    </button>
  );
  return (
    <section className="admin-section">
      <div className="crt-head">[ CHEATS ]</div>
      <p className="admin-hint">only for seats signed in as admin — nobody else in the lobby is affected and no shared payload changes. resets to off when the server restarts.</p>
      <div className="cheat-list">
        <Row k="revealAi" label="reveal the AI" blurb="your screen shows which seat is the AI at game start (a private badge under the round header)." />
        <Row k="infiniteTokens" label="infinite fake-out tokens" blurb="wrong accusations and vetoes don't cost you a token." />
      </div>
    </section>
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

function DangerZone({ onWipe, onPurge }) {
  return (
    <section className="admin-section danger-zone">
      <div className="crt-head danger-head">[ DANGER ZONE ]</div>
      <div className="danger-box">
        <p className="danger-copy">
          delete every guest + legacy password account (keeps games and oauth logins).
        </p>
        <button className="danger-btn" onClick={onPurge}>delete non-oauth users…</button>
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
