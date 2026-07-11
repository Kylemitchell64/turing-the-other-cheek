# 0008 — rate limiting keyed on the JWT, not the IP

## context

I got a rude lesson in why open APIs need limits on my last project (someone found the endpoint
and started flooding oversized payloads), so this API rate-limits from day one. The default and
obvious key is the client IP — 30 requests a minute per IP, return 429 over that. Except this is
a *party* game. The whole point is a room full of people playing together, and a room full of
people is on **one wifi network**, which is **one public IP.** Key the limiter on IP and eight
phones in a living room split a single 30/min bucket between them — the game rate-limits its own
players for using it as intended.

## decision

Key the fixed-window limiter on the **authenticated user** when there is one, and fall back to
IP only for the handful of pre-auth requests. In `Program.cs` the global partitioned limiter
pulls the user id claim (`ClaimTypes.NameIdentifier`) off the JWT and partitions on that, so
every player gets their own 30/min window regardless of whose wifi they're on. Login/register
(no token yet) share the per-IP bucket, which is correct — that's exactly the traffic you *do*
want to cap per source.

A few paths are exempt from the limiter entirely, by partition:

- `/hubs` — the SignalR traffic (negotiate + the live game) can't run on a 30/min budget.
- the admin routes — gated separately by the admin policy.
- the public status probe — so the UptimeRobot keepalive (ADR 0001) never gets throttled.

## consequences

- A living room of players is no longer one throttled IP; each authenticated player has their
  own budget. This was the bug that would've made the game feel broken at exactly the moment it's
  supposed to shine (everyone playing at once).
- Abuse protection is actually *better* this way, not worse — a limit tied to identity is harder
  to work around than one tied to a shared, NAT'd IP, and issuing a token costs an OAuth round
  trip.
- The one thing to keep an eye on: a determined abuser can mint many guest tokens to get many
  buckets. Guest creation itself is the natural choke point for that, and guests are retention-
  swept, so it's bounded — but if this were a real product that's where I'd add the next limit.
