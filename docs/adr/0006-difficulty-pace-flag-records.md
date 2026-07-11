# 0006 — difficulty and pace as flag records, not scattered ifs

## context

The host can pick how sneaky the impostor is (easy / normal / hard) and how long the answer
window is (flash through snail). The easy way to build that is to sprinkle
`if (difficulty == "hard")` checks through the prompt builder, the post-processor, and the
timing code. That's also the way you end up with three difficulty levels that disagree with
each other, because the branches drift as you touch each file.

## decision

Make difficulty **one immutable record of flags**, defined once, read everywhere.
`DifficultyProfile` (in `GameOptions.cs`) is a record with a named boolean (or small value) for
each individual knob the disguise has:

- `UseStyleSummaries` — inject the learned per-player style notes into the prompt.
- `TrimSharpRules` — drop the sharpest countermeasure rules (statistical-middle,
  low-effort-answers, same-round-similarity).
- `EasyPersona` — swap the whole disguise prompt for a short "polite party guest," letting the
  default AI voice bleed through. Easy only.
- `InjectTypos`, `ConformToGroup` — the post-processing knobs.
- `AllowDeadlineScrape`, `FixedTimingBand` — the timing knobs.

Then the three levels are just three constant instances that switch those flags on and off.
`Easy` is deliberately catchable (generic persona, tight predictable timing band — suspicious
consistency IS the tell). `Hard` is the full AI-DESIGN treatment. `Normal` sits in between.
Pace is the same idea in `PaceOptions` — a name→seconds lookup, with a helper that flags the
short windows so the AI knows nobody types a paragraph in 10 seconds.

## consequences

- Every consumer (prompt builder, post-processor, timing) reads a flag off the profile instead
  of re-deriving the difficulty. There's exactly one place that decides what "hard" means, so
  the levels can't silently disagree.
- Adding a fourth level, or a "hard timing but easy prompt" combo, is a one-line new record —
  no new branches anywhere.
- The flags are directly testable: a test can assert `Hard.InjectTypos` is true without
  standing up a whole game, and the difficulty behavior tests read cleanly.
- Both records live on the in-memory `Lobby` and ride the existing `SetLobbyOptions` /
  `LobbyOptionsChanged` plumbing — same path as the prompt pack, so the host UI wiring was
  already there.
