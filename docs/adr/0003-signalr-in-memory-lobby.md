# 0003 — SignalR hub + in-memory lobby store

## context

The game is real-time: 3–8 phones need to see the same countdown, the same reveal, the same
accusation window, all within a fraction of a second of each other. And the server has to own
the clock — if I trusted client timers, a player could freeze their phone to dodge a deadline,
or lie about when they answered (timing is one of the ways you catch the AI, see ADR 0005).

## decision

Everything runs over a single SignalR hub, `/hubs/game`. Lobby and in-progress game state live
**in memory** in `LobbyStore` — a `ConcurrentDictionary<string, Lobby>` keyed by the 5-char join
code. Each lobby has its own `Sync` lock that the hub takes for any state change, so two players
acting at once can't corrupt a lobby. A hosted background service ticks each lobby's state
machine and fires every timer server-side; clients just render what the server broadcasts.

The database is only touched at **game end**. That's when the finished game, its messages,
per-player stats, and each player's answers (harvested into their writing samples) get written
in one go. Nothing about a live lobby round-trips to Postgres.

## consequences

The obvious cost: **if the server restarts, every in-progress lobby is gone.** A Render deploy,
a crash, or the free tier spinning down mid-game all wipe the store. I decided that's an
acceptable loss and didn't build persistence for it, because:

- A game is one sitting, a few minutes long. The blast radius of a restart is "the group
  reopens the app and starts a new lobby," not lost data — nothing that matters was ever only
  in memory, since finished games are already in the DB.
- Persisting live state would mean writing lobby snapshots to Postgres on every tick, which on
  a free-tier session pooler is exactly the write load I'm trying to avoid (ADR 0001).
- Reconnects inside a live game already work without persistence: a player who drops and comes
  back with the same user id folds back into their existing seat, because the lobby is still in
  memory on the server. It's only a full server restart that ends things, and I keepalive-ping
  the server specifically so it doesn't idle-restart under a live game.

So: in-memory is the feature, not a shortcut. It's what lets one small free box run the whole
thing, and the only thing it can't survive is a process restart — which is rare, quick to
recover from, and costs nobody anything real.
