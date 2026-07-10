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
| Deploy | Render (API), Vercel (frontend), UptimeRobot (keepalive) |

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

## Tests & CI

Every push and PR runs the whole thing through GitHub Actions — the .NET suite (130 tests on EF InMemory, no DB needed), the client lint + build, a Docker image build, and a 4-browser Playwright game played start to finish. Green badge above means all of that passed on `main`.

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

### UptimeRobot (keepalive)

- New HTTP(s) monitor on `<your Render URL>/api/health`, interval 5 minutes.
- Why: Render's free tier spins the service down after ~15 min idle, and Supabase pauses
  a free project after 7 days of no activity. `/api/health` runs a `SELECT 1` against the
  DB, so this one ping keeps *both* awake — no cold starts, no paused database.

Full click-by-click walkthrough is in `DEPLOY.md`.

## Architecture notes

Realtime runs over a single SignalR hub (`/hubs/game`), JWT passed as a query-string token
on the handshake. Lobby and game state live **in memory** in a `ConcurrentDictionary`
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
