# 0004 — JWT on the hub query string, token in sessionStorage

## context

Two auth questions that both have a slightly non-obvious answer:

1. SignalR's WebSocket handshake can't carry a normal `Authorization: Bearer` header — the
   browser WebSocket API doesn't let you set headers. So how does the hub know who's connecting?
2. Where does the client keep the JWT between page loads?

## decision

**Hub auth via query string.** The client passes the JWT as `?access_token=...` on the hub
connection, and the JWT bearer setup in `Program.cs` has an `OnMessageReceived` handler that,
for requests to the `/hubs` path, pulls the token off the query string and hands it to the
normal validation pipeline. Everywhere else still uses the standard header. This is the pattern
Microsoft documents for SignalR precisely because of the header limitation — it's not a hack,
it's the supported path.

**Token in `sessionStorage`, not `localStorage`.** The client keeps the JWT in `sessionStorage`
under `ttoc-token` (`AuthContext.jsx`), and rejects it on load if it's already expired.

## consequences

On the query-string token: a token in a URL can end up in server access logs, so this only
works because the token is short-lived and the connection is HTTPS end to end. For a hub
handshake it's the right call; I wouldn't put it on a regular GET.

On `sessionStorage` vs `localStorage` — I went back and forth on this one:

- I first tried keeping the token **in memory only** (cleanest, nothing persisted). Phones
  killed it. Mobile browsers discard backgrounded tabs aggressively, and every discard — or an
  accidental pull-to-refresh — dumped the player back to the login screen mid-game. Unusable.
- `localStorage` would survive all of that, but it's shared across every tab and sticks around
  for ~30 days on the OAuth session. For a party game that's more persistence than I want: a
  shared phone, a borrowed laptop, and now someone's logged in as you for a month.
- `sessionStorage` is the middle: it's **per-tab and dies with the tab**, so it survives a
  reload or a backgrounding within the same session but doesn't leave a logged-in tab lying
  around forever. Combined with the expiry check on load, a stale token is never used.

The one edge case is private/blocked storage — the code catches the throw and just stays
memory-only in that case, so the app still works, it just won't survive a reload.
