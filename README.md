# Turing the Other Cheek

[![CI](https://github.com/Kylemitchell64/turing-the-other-cheek/actions/workflows/ci.yml/badge.svg)](https://github.com/Kylemitchell64/turing-the-other-cheek/actions/workflows/ci.yml)

A social deduction party game where one player in every lobby is secretly an AI, and
everyone else is trying to figure out who. The twist: the AI actually learns how you
write over time, so the more you play the harder it gets to catch.

Built it as a portfolio piece — real-time multiplayer, an AI that has to blend in, and
a game-theory-flavored veto rule that keeps players from leaking information.

## Tech stack

| Layer | Tech |
|-------|------|
| Frontend | React 19 + Vite (plain .jsx, no TypeScript), React Router |
| Backend | ASP.NET Core 8, EF Core 8, SignalR |
| Database | PostgreSQL (Supabase free tier) |
| Auth | ASP.NET Core Identity + JWT bearer |
| AI player | Google Gemini (`gemini-2.5-flash`, free tier) behind an `IAiBrain` interface |
| Containers | Docker (multi-stage, serves the client from wwwroot) |
| Deploy | Render (API), Vercel (frontend), GitHub Actions cron (gentle keepalive) |

Backend is in `GameApi`, the phone-first React client is in `game-client`.

## How the game works

- Host makes a lobby, gets a 5-character join code. 3–8 people join on their phones.
- On start the server drops in one AI player under a fake human first name (roster gets
  shuffled so it's never obviously last). Everyone starts with **3 fake-out tokens**.
- Each round everyone answers the same casual prompt ("worst purchase you ever made")
  on a 30-second timer. The AI answers too, with a randomized human-looking delay.
- Answers reveal, keyed only by display name. Then a 20-second accusation window — anyone
  can accuse one player of being the AI. First accusation locks the window.
- Here's the important rule: an accusation **doesn't reveal the result right away**. It
  opens a 10-second veto window. Any *other* player with a token can burn one to overrule
  the accuser and keep the game going. If they do, everyone's screen shakes and **the
  result is never shown** — because if vetoes only happened on correct guesses, vetoing
  would itself confirm the AI. A veto costs one full cooldown round (no accusations for
  anyone), then the vetoer gets a 5-second priority accusation window.
- No veto → result revealed. Correct = you win as the Detector, full AI reveal. Wrong =
  you burn a token. At 0 tokens a wrong accusation makes you answer-only.
- Game ends on a correct un-vetoed accusation (Detector win), or after 8 rounds / all
  humans eliminated (AI survives).

**On your own?** The home screen has a **solo demo**: three bot stand-ins get seated with
you and the AI still hides among them. Five rounds, nothing saved, same rules — enough to
see the trick without rounding up friends. Hosts also get a **QR code + share link** in the
lobby so phones can scan in instead of typing the code, and every game ends on a **recap**:
the AI's line for each prompt, every accusation and how it went, the style notes the AI was
carrying on each player, and a share button that hands your phone a result card. In the
solo demo a bot will even accuse someone and get faked-out by another bot, so you see the
veto rule play out without anyone explaining it.

There's also a **reverse mode**: no hidden impostor — everyone's human, and the *AI* is the one
guessing who wrote which (shuffled, anonymous) answer each round. It's the same style-profile
tech pointed the other way, so it's gated on having a bit of play history (see the ADRs). The
whole client is phone-first and got a dedicated mobile visual sweep so nothing breaks on a small
screen.

## Tests & CI

Every push and PR runs the whole thing through GitHub Actions — the .NET suite (240 tests on EF InMemory, no DB needed), the client lint + build, a Docker image build, and a Playwright game played start to finish across desktop and mobile viewports. Green badge above means all of that passed on `main`.

## Load test

There's a [k6](https://k6.io) script in `loadtest/k6-lobbies.js` that mimics the path a
real client takes into a lobby — health ping, guest login for a JWT, then the SignalR
`/negotiate` handshake — and runs it from 20 concurrent virtual users. Point it at a local
API on the in-memory DB + Mock brain (see the header comment in the script), then:

```
k6 run loadtest/k6-lobbies.js
```

Latest local run (loopback, in-memory DB, 20 VUs over ~35s):

| Metric | Result |
|--------|--------|
| Requests | 1,665 total, **0 failed** |
| Throughput | ~46 req/s (555 full login→negotiate cycles) |
| Latency | avg 1.3 ms, **p95 2.4 ms**, max 297 ms |
| Checks | 2,775 / 2,775 passed |

These are a floor, not a headline — it's all over the loopback with an in-memory store, so
there's no network or Postgres in the path. What I actually wanted to prove is that login +
the negotiate handshake stay flat under concurrency and nothing 500s. One gotcha worth
knowing: the API rate-limits 30 req/min per IP, and since k6 all comes from one IP you have
to bump `RateLimit__PermitsPerMinute` for the run or you just measure the limiter (the
`/hubs` path is exempt, so negotiate sails through either way).

## Running it locally

You need .NET 8 SDK, Node 22, and a Postgres connection string (Supabase free works).

**API:**
```
cd GameApi
dotnet ef database update      # applies migrations to your DB
dotnet run
```
Runs on http://localhost:5222 by default.

**Client:**
```
cd game-client
npm install
npm run dev
```
Runs on http://localhost:5173, talks to the API on 5222.

### Environment variables

The API reads secrets from env vars (or `GameApi/.env` locally — gitignored). None of
these are ever committed.

| Var | What it is |
|-----|-----------|
| `ConnectionStrings__DefaultConnection` | Supabase Postgres string, Npgsql key-value format (`Host=...;Port=5432;Database=...;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true`) |
| `GEMINI_API_KEY` | Google AI Studio key for the AI player. Without it the app falls back to the Mock brain, so it still runs. |
| `JWT_KEY` | Signing key for JWTs, 64+ random chars |
| `Cors__AllowedOrigins__0` | Allowed frontend origin (your Vercel URL in prod). Defaults to `http://localhost:5173`. |
| `Ai__Brain` | `Gemini` or `Mock`. Defaults to Gemini when a key is present, else Mock. |

Client:

| Var | What it is |
|-----|-----------|
| `VITE_API_URL` | Where the API lives, e.g. `https://your-service.onrender.com`. Leave it unset when the API serves the client itself (the Docker image) — the client then talks same-origin. |

## Deploying

The Docker image is self-contained — it builds the React client and serves it out of
`wwwroot`, so the image alone is fully playable. In the real deploy the frontend also
lives on Vercel (faster static hosting, its own domain) and points at the Render API.

### Render (the API)

- New Web Service → connect the GitHub repo. It auto-detects the `Dockerfile`.
- Runtime: **Docker**. Instance type: **Free**. Region: US East.
- Environment variables:
  - `ConnectionStrings__DefaultConnection`
  - `GEMINI_API_KEY`
  - `JWT_KEY`
  - `Cors__AllowedOrigins__0` = your Vercel URL (add after Vercel is up, then redeploy)
- Render sets `PORT` itself (10000 for Docker). The app binds to it automatically —
  don't set `ASPNETCORE_URLS`.

### Vercel (the frontend)

- Import the same repo. **Root Directory: `game-client`** (Vercel auto-detects Vite).
- Env var: `VITE_API_URL` = your Render URL.
- Deploy, then go back to Render and set `Cors__AllowedOrigins__0` to the Vercel URL and
  redeploy so CORS lets the Vercel origin through.

### Keepalive (deliberately gentle)

- `.github/workflows/keepalive.yml` pings `<Render URL>/api/health` **four times a day**
  from GitHub Actions. That's enough to reset Supabase's 7-day idle clock and nothing more.
- Do **not** add a frequent pinger (UptimeRobot every 5 min, etc.). Render's free tier is
  750 instance-hours per month for the whole account; a service that never sleeps burns
  ~744 of them alone and takes every other free service with it. Been there.
- The trade: the first visitor after an idle stretch waits ~30-60s for a cold start. The
  client shows a `[ WAKING UP ]` note while that happens.

### If Supabase pauses anyway

The API doesn't go down. If Postgres doesn't answer at boot, it comes up on an in-memory
store instead: fully playable, nothing saved, a `[ TEMP MODE ]` banner on the login and
home screens, and `storage: "memory"` on `/api/status` and `/api/health`. Restore the
project in the Supabase dashboard and the API restarts itself onto Postgres within a few
minutes, between games. Details in [ADR 0010](docs/adr/0010-memory-fallback-when-postgres-is-down.md).

Full click-by-click walkthrough is in `DEPLOY.md`.

## Architecture notes

Realtime runs over a single SignalR hub (`/hubs/game`), JWT passed as a query-string token
on the handshake. A dropped socket (phone lock, tunnel) auto-reconnects and calls `Rejoin`,
which re-attaches the new connection to the player's seat and returns a full state snapshot
so the screen rebuilds mid-round instead of freezing. Lobby and game state live **in memory** in a `ConcurrentDictionary`
keyed by join code, each lobby behind its own lock — a hosted background service ticks the
state machine and fires all timers server-side, so client clocks are never trusted. The DB
only gets written at game end (the game, messages, per-player stats, and each player's
answers harvested into their writing samples).

The AI player sits behind an `IAiBrain` interface with two implementations: `GeminiBrain`
(real, calls Gemini over raw HTTP) and `MockBrain` (canned answers, used by the tests so
they never hit the network). Its answers run through a post-processing pipeline that makes
them match the group's average length, capitalization and punctuation habits, sprinkles in
the occasional typo, and drops anything AI-flavored — the goal is the statistical middle of
the room, never the funniest or most polished. **Style profiles** are the killer feature:
after each game a player's answers are appended to their sample pool, a background job asks
Gemini to summarize how they write into a compact JSON blob, and those summaries get injected
into the AI's system prompt at the next lobby start. The more you play, the better it copies you.

The whole thing is audited so the AI's identity never leaks in any payload before game end —
rosters carry no user IDs, revealed answers are keyed by display name only and shuffled, and
the veto rule exists specifically so a veto can't confirm a correct guess.

## Operator console

`/admin` (Google sign-in on the `ADMIN_EMAILS` allowlist) has, besides the analytics tiles
and user directory:

- **Self-check** — runs the real chain and reports each step live: database + which store
  we booted on, pending migrations, config (JWT length, CORS, which AI legs have keys and
  their breaker state), one real AI completion, a **synthetic solo game** on fast clocks
  (bots seat, everyone answers, the AI answers, a scripted accusation gets faked-out, the
  game ends, nothing persisted), the lobby store, and free-tier headroom (warn at 75%,
  fail at 90% of any cap). One run at a time.
- **Users** — filter (inactive 30d+, guests, oauth, *safe to delete*: guests that never
  played, hold no samples and haven't been seen in 24h) and sort (last seen, storage,
  games, name). Storage is the account's real stored bytes: samples + messages + profile +
  character.
- **Cleanup** — preview first, then `CLEANUP` to confirm: removes safe-to-delete accounts,
  guests past the 30-day retention rule, consumed rewards older than 90 days, and dead
  in-memory lobbies. Transcripts are kept (author links nulled).
- **Cheats** — for admin seats only, off after every restart: *reveal the AI* (a private
  badge on your screen; no shared payload changes) and *infinite fake-out tokens*.

The engine also sweeps dead lobbies on its own: a finished game with nobody attached is
dropped at once, an abandoned one after 10 minutes, so memory doesn't grow across the month.

## Engineering notes

The interesting decisions are written up as short ADRs in [`docs/adr/`](docs/adr/) — the
free-tier-only stack, the Gemini → Groq → Cerebras failover chain, why lobby state lives in
memory, the JWT/sessionStorage auth choices, how the AI stays anonymous in every payload,
difficulty as flag records, signed pack share codes, per-user rate limiting, reverse mode, the in-memory fallback that keeps the game playable when the free-tier database is paused, and the solo demo / rejoin design.

The anonymity side has its own [threat model](docs/threat-model.md) — the ways a player could try
to unmask the AI (frame sniffing, timing, statistical tells, host abuse, cross-rematch replay)
and what the code does about each, plus the ordinary web hardening (auth, rate limits, admin gate,
pack-code signing, prompt-injection guardrails).
