# Deploy walkthrough

Click-by-click for getting this live: Render (API) → Vercel (frontend) → UptimeRobot
(keepalive). This lines up with MORNING.md steps 3–5. Budget ~15 minutes. Everything is
free tier, no card.

Before you start you need the three secrets (Supabase connection string, Gemini key,
JWT key) in `GameApi\.env` and the repo pushed to GitHub. If the secrets aren't in yet,
do MORNING.md steps 1–2 first.

---

## 1. Render — the API + database-backed backend

Go to https://dashboard.render.com and log in with GitHub.

1. Top right: **New +** → **Web Service**.
2. **Connect a repository** → pick `Kylemitchell64/turing-the-other-cheek`. If it's not
   listed, click **Configure account** / grant the Render GitHub app access to the repo,
   then come back and select it.
3. On the settings page:
   - **Name**: `turing-the-other-cheek` (this becomes your URL:
     `https://turing-the-other-cheek.onrender.com`)
   - **Region**: Oregon (US West) or Ohio (US East) — either is fine.
   - **Branch**: `main`
   - **Runtime / Language**: it should auto-detect **Docker** from the `Dockerfile` at the
     repo root. If it shows a language dropdown, pick **Docker**. Leave build/start commands
     blank — the Dockerfile handles both.
   - **Instance Type**: **Free**.
4. Scroll to **Environment Variables** → **Add Environment Variable**. Add these, copying
   the values from `GameApi\.env`:

   | Key | Value |
   |-----|-------|
   | `ConnectionStrings__DefaultConnection` | your Supabase session-pooler string |
   | `GEMINI_API_KEY` | your Gemini key |
   | `JWT_KEY` | the 64-char key from .env |

   > Leave `Cors__AllowedOrigins__0` out for now — you don't have the Vercel URL yet. You'll
   > add it in step 3. The app boots fine without it (CORS just defaults to localhost).

   Note the double underscores (`__`) — that's how .NET reads nested/array config from env
   vars. `Cors__AllowedOrigins__0` is index 0 of the `Cors:AllowedOrigins` array.

5. Click **Create Web Service**. First build takes a few minutes (it builds the React
   client, publishes the API, assembles the image). Watch the log; you want "Your service
   is live".
6. Copy your URL from the top of the page, e.g. `https://turing-the-other-cheek.onrender.com`.
7. Sanity check: open `https://<your-render-url>/api/health` in a browser. You want
   `{"status":"ok","db":true}`. If `db` is `false`, the connection string is wrong — fix it
   in Environment and let it redeploy. (You can also just open the root URL — the game's
   login screen should load, served straight from the image.)

> **Render note:** the free tier spins down after ~15 min idle. The first request after a
> spin-down takes ~30–50s to wake. UptimeRobot (step 3) prevents this.

---

## 2. Vercel — the frontend

Go to https://vercel.com and log in with GitHub.

1. **Add New...** → **Project**.
2. Find `turing-the-other-cheek` in the list → **Import**. Grant the Vercel GitHub app
   access to the repo if it asks.
3. On the configure screen:
   - **Framework Preset**: should auto-detect **Vite**.
   - **Root Directory**: click **Edit** and set it to **`game-client`**. This is important —
     the React app isn't at the repo root.
   - Build command / output dir: leave as the Vite defaults (`npm run build`, `dist`).
4. Expand **Environment Variables** and add:

   | Key | Value |
   |-----|-------|
   | `VITE_API_URL` | your Render URL from step 1, e.g. `https://turing-the-other-cheek.onrender.com` |

   No trailing slash.
5. Click **Deploy**. When it's done, copy your Vercel URL, e.g.
   `https://turing-the-other-cheek.vercel.app`.

---

## 3. Close the CORS loop (back on Render)

The API only accepts requests from origins in its CORS allow-list. Right now that's just
localhost, so the Vercel site can't talk to it yet. Fix that:

1. Back in the Render dashboard → your service → **Environment**.
2. **Add Environment Variable**:

   | Key | Value |
   |-----|-------|
   | `Cors__AllowedOrigins__0` | your Vercel URL, e.g. `https://turing-the-other-cheek.vercel.app` |

   Exact origin, no trailing slash, `https://`.
3. Save. Render redeploys automatically. Wait for "live".

> **Why the loop:** the API needs the Vercel origin, but the Vercel URL doesn't exist until
> after you deploy Vercel — which needs the Render URL. So the order is: deploy Render →
> deploy Vercel with the Render URL → add the Vercel URL back to Render's CORS → redeploy.
> If login on the live site fails with a CORS error in the browser console, this variable is
> missing, has a typo, or has a trailing slash.

---

## 4. UptimeRobot — keepalive

Go to https://uptimerobot.com and log in.

1. **+ New monitor**.
2. Settings:
   - **Monitor Type**: HTTP(s)
   - **Friendly Name**: `turing health`
   - **URL**: `https://<your-render-url>/api/health`
   - **Monitoring interval**: 5 minutes
3. **Create Monitor**.

This hits `/api/health` every 5 minutes. That endpoint runs a `SELECT 1` against Postgres,
so the single ping keeps the Render service awake **and** stops Supabase from pausing the
free project after 7 idle days. Both problems, one monitor.

---

## 5. Play

Open your Vercel URL on 3+ phones, make accounts, one person creates a lobby and shares the
code, everyone joins, host hits Start. Catch the AI.

If something looks different from these steps, the dashboards change their UI now and then —
the field names above (Docker runtime, Root Directory `game-client`, the four env vars) are
what matters; find the equivalent box.
