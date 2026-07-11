# 0007 — signed share codes for custom packs

## context

Players can generate their own prompt pack from a theme (the AI-built "make your own" packs),
and then share it with friends as a short code they paste into a lobby. I didn't want to store
these on the server — that's a table, a cleanup job, and moderation I don't want to own for a
free-tier hobby project. So the whole pack has to live *inside the code itself*: the code is
the pack. That raises the obvious problem — if the pack is just encoded in the string, anyone
can hand-edit the string and smuggle in prompts that never went through the content filter.

## decision

The share code is a **signed, compressed blob**, not encrypted (`PackCodec`). The pack is
serialized, gzipped, and a truncated **HMAC-SHA256** signature is appended:

```
sig = base64url( HMAC-SHA256(key, gzipBytes)[..16] )
```

The signing key is **derived** from `JWT_KEY` (an HMAC of the constant string `"packcode"`), so
the pack secret and the auth secret aren't literally the same value but there's only one secret
to configure. On decode, the codec recomputes the signature and rejects anything where it
doesn't match — wrong shape, tampered bytes, oversize, or junk all come back as a friendly error.

## consequences

The key decision is **signing, not encryption**, and it's deliberate:

- The pack content isn't secret. It's a list of party prompts the player wrote and wants to
  share — there's nothing to hide, so encrypting it would buy nothing.
- What I actually need is **integrity**: proof that the pack in this code is exactly the pack my
  server generated and content-filtered, byte for byte. A signature gives me that. If someone
  edits the blob to inject a banned prompt, the HMAC no longer matches and the code is rejected
  before it's ever used.
- The content filter (`PackGenerator`) runs at generation time, and the signature is what makes
  that check *stick* — you can't launder unfiltered content through a hand-crafted code, because
  you can't forge the signature without `JWT_KEY`.

So no server storage, no moderation queue, and no way to tamper. The tradeoff is that packs
can't be revoked or updated after they're shared (the code is immutable), which for user-made
party packs is completely fine.
