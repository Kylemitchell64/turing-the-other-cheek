# 0001 — everything runs on free tiers

## context

This is a portfolio project, not a business. I wanted it live on the internet, playable
from a phone, with a real database and a real AI player behind it — and I wanted the
monthly bill to be $0. So every piece had to fit inside somebody's free tier.

## decision

- **API** on Render, deployed as a Docker image. The Dockerfile is multi-stage: it builds
  the React client and copies it into `wwwroot`, so the single image is fully playable on
  its own. Vercel is just a faster front door.
- **Frontend** on Vercel, pointed at the Render API. Static hosting, its own domain, instant
  cache.
- **Database** is Postgres on Supabase's free tier. The API talks to it through Supabase's
  **session pooler** (port 5432, the pgbouncer endpoint), not a direct connection — the free
  tier only hands out a handful of direct connections and Render can spin up more than that.
- **UptimeRobot** pings `/api/health` every 5 minutes to keep everything warm (see below).

## consequences

The free tiers all have the same catch: they go to sleep. Render spins a free service down
after ~15 min of no traffic, and the next request eats a cold start (~30–50s while the
container boots). Supabase pauses a project after 7 days of zero activity, and an unpausing
DB is worse. Both of those would make the game feel broken to whoever clicks the link.

The fix is one cheap heartbeat. `/api/health` runs a `SELECT 1` against the DB via the
`DatabaseHealthCheck`, so a single UptimeRobot ping keeps the API *and* the database awake
at the same time. That status probe is exempt from the rate limiter and from auth on purpose,
so the keepalive never gets throttled or 401'd.

Tradeoffs I accepted:
- Cold starts still happen if UptimeRobot ever misses (first player of the day might wait).
  That's fine for a demo, would not be fine for real users.
- The session pooler means no true prepared statements and a shared connection layer, but for
  this workload (writes only at game end) it's a non-issue.
- Free Render is a single small instance. The whole point of keeping lobby state in memory
  (see ADR 0003) is that one box can hold a party game's worth of state without a second
  service to pay for.

If this ever needed to be a real product, the migration is boring and known: paid Render
instance, direct Postgres pool, drop the keepalive. Nothing here paints me into a corner.
