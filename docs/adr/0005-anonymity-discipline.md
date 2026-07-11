# 0005 — the anonymity discipline

## context

The entire game rests on one promise: **before the endgame reveal, nothing the server sends
can tell a player which seat is the AI.** Not the answer text, not the payload shape, not the
timing, not some field that's only ever set for one player. It doesn't matter how clever the
AI's writing is if a player can pop open devtools, watch the WebSocket frames, and see the bot
labeled. This ADR is the discipline that keeps every payload uniform. (The impostor's *writing*
is ADR 0002 and AI-DESIGN.md; this is about everything *around* the writing.)

## decision

Treat the AI as just another seat in every payload until the game is decided.

- **No user ids on the roster.** Rosters and revealed answers are keyed by display name only.
  The AI joins under a fake human first name pulled from a ~40-name pool in `LobbyStore`, and
  `StartGame` picks one that doesn't collide with a real player's name. There's no `isAi` flag,
  no null user id, no "bot" marker anywhere a client can see.
- **Shuffled seating.** The roster is Fisher-Yates shuffled on start so the AI is never
  predictably last (the naive "append the bot" bug). Answers are shuffled at reveal too.
- **Every seat carries a character sprite — including the AI.** A human without a saved
  character gets a deterministic **name-hash default** (`CharacterDefaults.FromName`, an FNV-1a
  hash ported byte-for-byte from the client's `sprites/config.js`). The AI can never save a
  character, so it *always* resolves to its fake name's hash default. Crucially, a human who
  also never customized looks identical in kind — a hash-derived look. So "has a default sprite"
  doesn't finger the bot, because plenty of humans have one too. Same math on both ends means
  the client and server always agree on the look without the server sending anything extra.
- **Fake human typing delays.** The AI never answers instantly. Its send time is computed from
  a timing model (`AnswerTiming`) with a typing-speed floor, a gaussian human band, and an
  occasional last-second "deadline scrape" — with an anti-pattern guard so it doesn't answer at
  a suspiciously consistent moment every round. To the server's broadcast, the AI's answer
  arrives like anyone else's.
- **Host-driven fields stay host-driven.** Options the host sets (pack, pace, difficulty, music)
  live on the lobby and broadcast identically to all seats. There's no per-player field that
  exists for the AI and not for humans.

## consequences

This constraint touches almost every payload, so it's the thing most likely to regress. That's
why it's backed by tests — `CharacterHashTests` pins the hash to the JS implementation, and the
roster/reveal tests assert no user id or AI marker leaks pre-endgame. The threat model
(`docs/threat-model.md`) walks the specific attacks (frame sniffing, timing analysis, tells)
and points at the class that defends each one. The full reveal — who the AI was — only ships
once the game is over and there's nothing left to protect.
