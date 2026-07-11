# 0009 — reverse mode (AI as the guesser)

## context

The classic game is humans-hunting-a-hidden-AI. The natural mirror is: no hidden AI at all —
everyone's a human, and the *AI* is the one guessing. Each round everyone answers, the answers
show up anonymous and shuffled, and the AI publicly calls which player wrote which. It's the same
tech (the learned style profiles) pointed the other direction, and it's a genuinely different
feel: instead of hiding from the bot, you're trying to write unlike yourself to fool it.

## decision

Reverse mode is a **game mode flag on the lobby**, not a separate game. `GameModes` (in
`GameOptions.cs`) has `Classic` and `Reverse`; the host picks it pre-start via the same
`SetLobbyOptions` path as everything else, and it persists across a rematch. The engine branches
on the mode: reverse has no impostor seat, so the roster is just the humans, and at each reveal
an `IAiGuesser` produces the attributions instead of the round running the classic
accuse/veto machinery.

The guesser mirrors the impostor's plumbing exactly. `GeminiGuesser` builds a strict-JSON prompt
from the round prompt, the anonymous answers, each player's learned style summary, and the
attributions it already got right in earlier rounds, then routes it through the **same
`IAiTextProvider` failover chain** the impostor uses (ADR 0002). It parses a JSON map of
answer-id → {name, taunt}. Same anti-stall rule as the impostor: an unparseable reply, a partial
reply, or a fully spent provider chain drops to a uniform **random-guess fallback** with generic
taunts, so a reveal *always* produces a complete set of attributions and the game never hangs.

## the gate — why reverse needs writing samples

The AI can only guess who wrote what if it knows how each player writes. That knowledge comes
from the style profiles built up over past games. So reverse mode is **gated at start**
(`GameHub.FindReverseNotReadyAsync`): every seat must be a **signed-in (non-guest) user with at
least 3 writing samples** (`MinReverseSamples`). Guests have no history, and a brand-new account
has nothing to profile — let either into a reverse game and the AI is guessing blind, which isn't
fun for anyone. If the readiness check can't reach the DB it **blocks the start** rather than
silently launching a game with no style data.

## consequences

- Reverse is a thin branch on top of the existing engine, provider chain, and options plumbing —
  not a parallel codebase. Most of it was already built for classic mode.
- The gate makes reverse a "you've played a few games" mode, which is the right unlock anyway —
  it's more interesting once the AI actually has a read on you.
- New DB columns came with it (`Game.Mode`, `PlayerStats.TimesReadByAi`) via a migration, default
  classic so existing rows are untouched.
