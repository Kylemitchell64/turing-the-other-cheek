# 0002 — AI provider failover chain

## context

The AI player runs on free API tiers, and free tiers run out. Gemini's free tier is a hard
per-day request cap — once you hit it you get 429s until midnight Pacific and there's no
paying your way out mid-game. If that happened during a lobby the impostor would go silent,
which is both a broken game and a dead giveaway. I needed the AI to keep answering even when
its primary provider is spent for the day.

## decision

One ordered chain of providers, all behind the same `IAiTextProvider` surface the game code
already called: **Gemini → Groq → Cerebras**. Gemini is its own client; Groq and Cerebras are
both OpenAI-compatible, so they share `OpenAiCompatClient` with different base URLs. The whole
thing is orchestrated by `AiTextProvider`, which:

- only includes legs that actually have a key configured (`HasKey`), in order;
- skips any leg the health tracker says is unavailable, and hops to the next one;
- returns the first success, logging every hop so I can see failover happening in the logs.

Health lives in `AiProviderStats` (one injectable singleton, one lock, an injected clock so
the tests can drive time). Per provider it tracks:

- a **circuit breaker** — 3 consecutive failures opens it for 5 minutes, then it half-closes
  and tries again. Keeps us from hammering a provider that's throwing.
- a **daily request counter** that resets at that provider's quota boundary (Gemini rolls
  over at Pacific midnight, the others I approximate at UTC midnight — it's a protective
  counter, not a billing number, so coarse is fine).
- an **exhausted-for-the-day** flag flipped by a 429. Once a provider says "you're out of
  quota" we stop asking it until its reset, instead of burning a real API call to rediscover
  that every round.

I also set **`thinkingBudget = 0`** on the Gemini calls (`GeminiClient`). The model wants to
emit hidden reasoning tokens before answering; for a one-line party answer that's pure latency
and pure quota burn. Turning it off makes answers land inside the human timing window and
stretches the free tier further. The disguise lives in the prompt, not in the model thinking
harder.

## consequences

- If the whole chain is spent (all three exhausted or breaker-open), `GenerateAsync` returns
  null and `GeminiBrain`'s canned fallback pool takes over. The AI still says something
  human-shaped, timed like a real answer — it never just stops.
- Three providers means three sets of keys to manage, but any one of them being down or capped
  is invisible to players.
- The `AiProviderStats` snapshot feeds the admin dashboard, so I can watch requests-today,
  failover hops, and breaker state per provider live.
