# 0010 — boot on an in-memory store when Postgres is unreachable

## context

Everything runs on free tiers (ADR 0001), and the sharpest edge on that stack is Supabase
pausing a project after 7 idle days. When that happened for real, the API was "up" — Render
served the client, `/api/health` returned 200 — but every login 500'd because Identity
couldn't reach its tables. From a visitor's phone the game was simply broken, and the only
fix was someone signing into the Supabase dashboard and clicking restore. For a portfolio
piece linked from a resume, "broken until the owner notices" is the worst possible state.

The app already had a Development-only `UseInMemoryDb` switch (Playwright and local smoke
runs use it), and the whole test suite proves the game plays end to end on EF InMemory. Lobby
and game state live in memory anyway (ADR 0003); the DB is only touched at login and at game
end. So a paused database is, mechanically, a small problem the app was treating as fatal.

## decision

At startup, before the DI container is built, `StorageMode.ProbePostgres` opens a real Npgsql
connection with a hard timeout (`Db:ProbeSeconds`, default 8) and runs `SELECT 1`. If that
fails, Program.cs registers `GameContext` on EF InMemory instead of Npgsql and records
`StorageMode = memory`. The game is fully playable: guest and password login, lobbies, the
AI, reverse mode, packs. Nothing persists past the process.

Three things make that honest rather than silent:

- `/api/status` and `/api/health` both carry `storage: "postgres" | "memory"`. Health keeps
  reporting `db: false` in memory mode even though the in-memory ping would "succeed", so the
  keepalive can still flag the outage.
- The client's status banner shows a soft `[ TEMP MODE ]` notice on the login and home
  screens: you can play, but stats and style profiles won't save this session.
- `PostgresRecoveryWatcher` (registered only in memory mode) re-probes Postgres every
  `Db:RecoveryProbeSeconds` (default 180). When it answers and no lobby is mid-game, it calls
  `StopApplication`. Render restarts an exited web service, and the new process comes up on
  Postgres. So the recovery path is: unpause Supabase, wait a few minutes, done — no redeploy,
  and never in the middle of a round.

The fallback is on by default and can be disabled with `Db:FallbackToMemory=false`. The
integration test factory disables it because it swaps the DbContext registration itself;
`StorageFallbackTests` boots a second factory with a dead connection string and asserts the
production path really does fall back, report itself, and log guests in.

A `keepalive.yml` GitHub Actions cron pings `/api/health` every 10 minutes alongside
UptimeRobot, so the 7-day idle window should never be reached in the first place. It warns in
the run log when it sees `db: false`.

## consequences

- A visitor never sees a dead game because of the free tier. Worst case they play without
  persistence and see a banner saying so.
- Memory mode is a real footgun if it goes unnoticed for weeks: accounts created then are
  gone on restart. That's why it is loud (critical log line, health flag, client banner,
  keepalive warning) rather than a quiet degrade.
- Accounts that already exist in Postgres can't log in with their password during memory
  mode (Identity is looking at an empty store). Guests just get a fresh guest account under
  the same name. Acceptable for a party game; the alternative was nobody playing.
- The restart-to-recover trick relies on the host restarting an exited container. Render
  does. Anywhere that doesn't, the process would stay down after recovery — set
  `Db:FallbackToMemory=false` there and treat a paused DB as an outage instead.
- The boot probe adds up to `Db:ProbeSeconds` to a cold start when Postgres is down. When
  it's up, it's one round-trip.
