# 0011 — solo demo with bot stand-ins, and rejoin after a dropped socket

## context

The game needs 3–8 humans in a room. A visitor arriving alone from a portfolio link hit a
wall at "need 2 more" and left without ever seeing the mechanic. Separately, phones lock:
SignalR's auto-reconnect brought the socket back, but the new connection id was not mapped
to the player's seat or lobby group, so the client came back to a frozen screen and missed
every event until the next game.

## decision

**Solo demo.** `StartSoloGame` (host alone, non-crew lobby) seats three bot stand-ins with
the human and the impostor AI, forces classic mode, and runs the ordinary engine. Bots:

- have `IsBot` seats with fake user ids, count as connected, and are removed again by any
  normal start (so a rematch with real friends just works);
- answer every round on the same typing-indicator + submit path the AI uses (`RunTypingAndSubmit`),
  from their own line bank (`BotAnswers`) so a demo doesn't sound like five copies of the
  mock brain, with delays spread across the window;
- never accuse or veto on their own initiative, are never `VetoEligible`, and never get a
  priority window;
- **except** for the scripted drama: from round 2, with `GameTimings:SoloDramaChance` per
  round and at most twice a game, a bot accuses a random seat a few seconds into the general
  window and a *different* bot fakes-out inside the veto window. The target can be anyone,
  even the AI, precisely because the veto rule (ADR 0005) guarantees a vetoed accusation
  reveals nothing. The visitor sees the accusation, the veto offer (they can burn a token
  first), the shake, and the blackout round — the whole rule in one demo.

Solo games cap at 5 rounds and are **never persisted**: bot seats have no user rows, and a
demo is not a stat. `GameEnded.IsSolo` tells the client to say so.

**Rejoin.** A `Rejoin` hub method finds the caller's seat by user id across all lobbies
(any state), attaches the new connection id, puts it back in the group, and returns a
`ResyncDto`: lobby, roster (re-shuffled with the AI never last, as always), phase, round,
prompt, deadline, whether this player already answered, the current reveal plus every past
round rebuilt from the transcript, open accusation / veto state as it applies to *this*
player, eliminations, tokens, and the `GameEnded` payload if it's over. The client calls it
on `onreconnected`, on `visibilitychange`/`online` (restarting the connection first if it's
dead), and shows a reconnect banner while the socket is down. The snapshot obeys the same
anonymity rules as the live events — names only, no author ids, no `isAi` before the end.

**Recap.** `GameEnded` also carries the accusation log (vetoed entries never carry
correctness), the AI's style notes per player, and the round prompts, so the end screen can
show "how the AI played it" and "what the AI had on you", and a share button can render a
result card. Only sent after the game is over, like the AI's identity.

## consequences

- A single visitor can play the real loop in about three minutes and see the veto rule
  without anyone explaining it.
- Bot answers are canned and will repeat across games; the pool is 40 lines with light
  noise, enough for a demo, not a substitute for humans.
- Scripted drama can accidentally "accuse" the AI. That's fine only because it's always
  vetoed; if the fake-out ever failed to fire (no second bot with a token), the accusation
  would resolve and reveal. The engine refuses to start a drama without a second token-holding
  bot, and solo keeps the veto window open even with no eligible humans.
- Rejoin re-shuffles past rounds, so a reconnected client sees answers in a different order
  than before. Order never carried information, so nothing leaks, but it is visible.
- Tests: `SoloModeTests` (bots seated and answering, 5-round solo end with forced drama,
  Rejoin snapshot) and Playwright `solo-demo.spec.js` / `reconnect.spec.js` (offline →
  banner → resync → game still advancing).
