# threat model

Most of this doc is about one unusual threat: **a player unmasking the AI before the game
decides it.** That's the thing that makes this game different from a normal web app, so it gets
most of the space. The boring web stuff (auth, rate limits, injection) is real and handled too,
it's just at the bottom.

The core promise, restated: before the endgame reveal, nothing the server sends may let a player
tell which seat is the AI. A player who breaks that promise ruins the game for everyone. So the
"attacker" here is usually just a curious player with devtools open, and the defense is that
there's genuinely nothing in the payload to find. See ADR 0005 for the design; this is the
attack-by-attack version.

## 1. sniffing the payload (devtools / WebSocket frames)

**Attack.** Open devtools, watch the SignalR frames, look for the field that marks the bot —
an `isAi` flag, a null user id, a "bot" name, a seat with no character.

**Defense.** There is no such field. Rosters and revealed answers are keyed by **display name
only** — no user ids, no AI marker (`GameHub`, `LobbyStore`). The AI joins under a real-looking
first name from a ~40-name pool, chosen so it doesn't collide with a human's name. Every seat
carries a character sprite, including the AI: humans without a saved one get a deterministic
name-hash default (`CharacterDefaults.FromName`), and the AI always resolves to that same kind of
hash default — so "has a default look" describes plenty of humans too, not just the bot. The
payload for the AI seat is shape-identical to a human seat. There's nothing to grep for.

## 2. timing analysis

**Attack.** The bot answers instantly, or always at the same moment, or suspiciously evenly.
Watch the clock instead of the words.

**Defense.** The AI never insta-sends. Send time comes from a human timing model
(`AnswerTiming`): a typing-speed floor scaled to answer length, a gaussian "normal human" band,
and an occasional last-second deadline scrape — plus an anti-pattern guard so it doesn't land at
the same offset every round. The one place predictable timing exists is **easy mode on purpose**
(`DifficultyProfile.FixedTimingBand`), where suspicious consistency is meant to be the tell that
makes easy catchable.

## 3. statistical / content tells

**Attack.** The bot writes too well, too long, too complete, too clever, too consistent — the
usual LLM giveaways.

**Defense.** This is the whole disguise pipeline (AI-DESIGN.md, ADR 0006). The prompt aims for
the statistical middle of *this* group, matches median length, copies the group's casing and
punctuation, is allowed to half-answer or dodge. Post-processing (`AnswerPostProcessor`) clamps
length, strips banned AI-flavored substrings (em dashes, "as an AI", "delve", etc.) with a
re-roll, conforms case and trailing periods to the group, and injects the occasional typo. On
harder difficulties the learned per-player style profiles get injected too, so the bot drifts
toward sounding like the actual people in the room.

## 4. host abusing the options

**Attack.** The host controls difficulty, pace, pack. Could they set something that outs the AI,
or read something the others can't?

**Defense.** Host-set options live on the lobby and broadcast **identically to every seat** —
there's no per-player or host-only field that would distinguish the AI (ADR 0005). The host picks
how hard the bot is, not who it is; the server never tells the host (or anyone) which seat is the
AI until the endgame reveal. The host is a player, not an admin.

## 5. replay / cross-rematch leakage

**Attack.** Play a rematch with the same group and correlate — same fake name, same seat, same
character each time would let you learn the bot across games.

**Defense.** The AI's fake name is re-picked and the roster is re-shuffled every game
(Fisher-Yates in `LobbyStore`/start), and answers are shuffled again at each reveal, so seat and
name don't carry information across rounds or rematches. Live state is in memory and the full
reveal only ships once a game is decided (ADR 0003), so there's no persistent per-game "the bot
was seat 3" record for a client to diff.

---

## the boring web stuff

Real, but standard — kept short.

- **Auth.** ASP.NET Core Identity + JWT bearer, social login (Google/GitHub) plus guests. Hub
  auth pulls the JWT off the `?access_token=` query string for the WebSocket handshake, header
  everywhere else (ADR 0004). Tokens are expiry-checked on load and kept in `sessionStorage`, not
  `localStorage`, so a logged-in tab doesn't linger.
- **Rate limiting.** Fixed-window limiter keyed on the **JWT user id**, not the IP, so a room of
  players on one wifi don't share a bucket (ADR 0008). Pre-auth login/register key on IP; `/hubs`,
  admin, and the status probe are exempt.
- **Admin gate.** Admin routes sit behind an `AdminOnly` policy — the `isAdmin` claim is minted
  only for Google accounts whose email is in `ADMIN_EMAILS` (`JwtTokenService` / `AdminEmails`).
  It's not a flag a client can set.
- **Pack share codes.** Signed with truncated HMAC-SHA256 (key derived from `JWT_KEY`), not
  encrypted — integrity, not secrecy (`PackCodec`, ADR 0007). A tampered or hand-crafted code
  fails signature check and is rejected, so unfiltered content can't be smuggled past the
  generator's content filter.
- **Prompt injection in the category maker.** The custom-pack generator (`PackGenerator`) wraps
  the untrusted user theme in delimiters as *data*, tells the model to return `REFUSED` for banned
  themes, and then — defense in depth — runs a **server-side wordlist/regex post-filter** over
  every returned line. Any banned hit anywhere drops the whole result. The signature (above) is
  what makes that filter stick after the fact.
