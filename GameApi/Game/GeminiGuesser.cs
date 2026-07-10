using System.Text;
using System.Text.Json;

namespace GameApi.GameLoop;

// The real reverse-mode guesser. Builds a strict-JSON prompt from the round prompt, the
// anonymous answers, each player's style summary, and the confirmed attributions from
// earlier rounds, then routes it through the shared failover chain (same IAiTextProvider
// GeminiBrain uses). Parses the JSON mapping answer-id -> {name, taunt}.
//
// It must NEVER stall the game: on an unparseable reply, a partial reply, or a spent
// provider chain, it drops to a uniform RANDOM-GUESS fallback with generic taunts. A
// reverse reveal always produces a full set of attributions.
public class GeminiGuesser : IAiGuesser
{
    private readonly IAiTextProvider _ai;
    private readonly ILogger<GeminiGuesser> _logger;

    public GeminiGuesser(IAiTextProvider ai, ILogger<GeminiGuesser> logger)
    {
        _ai = ai;
        _logger = logger;
    }

    public async Task<AiGuessResult> GuessAsync(AiGuessContext context, CancellationToken ct)
    {
        var rng = Random.Shared;
        try
        {
            var systemPrompt = BuildSystemPrompt(context);
            var userPrompt = BuildUserPrompt(context);
            var raw = await _ai.GenerateAsync(
                systemPrompt, userPrompt, temperature: 0.4, maxOutputTokens: 500, ct);

            var parsed = ParseGuesses(raw, context.Answers, context.PlayerNames);
            if (parsed != null) return parsed;

            _logger.LogInformation("Reverse guesser: unparseable reply, using random fallback");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // round ended — let the caller unwind
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reverse guesser call failed; using random fallback");
        }

        return RandomFallback(context.Answers, context.PlayerNames, rng);
    }

    // ---- parsing ----

    // Parse the model's JSON into a full, valid result, or null to signal "fall back to
    // random". We only accept a reply that maps EVERY answer id to a real player name —
    // anything partial or with an unknown name is safer to redo as a clean random pass.
    public static AiGuessResult? ParseGuesses(
        string? raw, IReadOnlyList<AnonAnswer> answers, IReadOnlyList<string> playerNames)
    {
        var json = ExtractJsonObject(raw);
        if (json == null) return null;

        // Case-insensitive name resolve back to the canonical display name.
        string? Resolve(string? guessed)
        {
            if (string.IsNullOrWhiteSpace(guessed)) return null;
            return playerNames.FirstOrDefault(
                n => string.Equals(n, guessed.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var guesses = new List<AiGuess>();
            foreach (var ans in answers)
            {
                if (!root.TryGetProperty(ans.Id, out var slot)) return null;

                string? name;
                string taunt;
                if (slot.ValueKind == JsonValueKind.String)
                {
                    // Tolerate a bare "id": "name" shape (no taunt).
                    name = Resolve(slot.GetString());
                    taunt = "";
                }
                else if (slot.ValueKind == JsonValueKind.Object)
                {
                    name = Resolve(
                        slot.TryGetProperty("name", out var n) ? n.GetString()
                        : slot.TryGetProperty("player", out var p) ? p.GetString()
                        : null);
                    taunt = slot.TryGetProperty("taunt", out var t) ? (t.GetString() ?? "") : "";
                }
                else
                {
                    return null;
                }

                if (name == null) return null; // unknown / missing name → redo as random
                guesses.Add(new AiGuess(ans.Id, name, string.IsNullOrWhiteSpace(taunt)
                    ? GenericTaunt(Random.Shared) : taunt.Trim()));
            }

            return new AiGuessResult(guesses);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Grab the outermost {...} block so a little pre/post chatter or a ```json fence
    // doesn't sink the parse.
    private static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return raw.Substring(start, end - start + 1);
    }

    // ---- random fallback ----

    // Uniform random assignment: every answer gets a real (possibly repeated) player name
    // and a generic taunt. The game must never stall on a dead provider chain, so this is
    // always a complete, valid result.
    public static AiGuessResult RandomFallback(
        IReadOnlyList<AnonAnswer> answers, IReadOnlyList<string> playerNames, Random rng)
    {
        var guesses = answers
            .Select(a => new AiGuess(
                a.Id,
                playerNames.Count > 0 ? playerNames[rng.Next(playerNames.Count)] : "someone",
                GenericTaunt(rng)))
            .ToList();
        return new AiGuessResult(guesses);
    }

    private static readonly string[] GenericTaunts =
    {
        "hmm, this one has 'chaos' written all over it",
        "gut says it's you, don't ask me why",
        "this reads exactly like someone i can't place",
        "coin flip, but i'm feeling confident",
        "something about the vibe here, ya know?",
        "i'd bet a nickel on this one",
        "the energy is unmistakable, allegedly",
        "no notes, just a hunch",
    };

    private static string GenericTaunt(Random rng) => GenericTaunts[rng.Next(GenericTaunts.Length)];

    // ---- prompts ----

    public static string BuildSystemPrompt(AiGuessContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append(
@"You are the AI in a party game, and this round your ONE job is to read the group and guess
which player wrote each anonymous answer. You lose if you get most of them wrong.

You will be given a prompt, a list of answers labelled a, b, c..., and short style notes on
each player. Match each answer to the ONE player most likely to have written it, using their
style notes and anything you learned from earlier rounds. Every answer maps to exactly one
player; a player may have written more than one across the game but only ONE per round.

For each answer add a SHORT, playful taunt aimed at the player you guessed — friendly ribbing,
never mean, always clean (SFW). Think 'gotcha, this is SO you' energy.

Output STRICT JSON and nothing else: an object keyed by answer id, each value an object with
""name"" (exact player display name) and ""taunt"" (one short line). Example:
{""a"":{""name"":""Sam"",""taunt"":""the lowercase gives you away every time""},""b"":{""name"":""Jo"",""taunt"":""too wholesome to be anyone else""}}");

        if (ctx.StyleSummaries.Count > 0)
        {
            sb.Append("\n\nSTYLE NOTES (how each player writes):\n");
            foreach (var line in ctx.StyleSummaries)
                sb.Append(line).Append('\n');
        }

        if (ctx.PriorAttributions.Count > 0)
        {
            sb.Append("\nWHAT YOU LEARNED SO FAR (your earlier guesses and the TRUTH):\n");
            foreach (var pa in ctx.PriorAttributions)
                sb.Append($"R{pa.Round} \"{pa.Text}\" — you guessed {pa.GuessedName}, actually {pa.ActualName} ({(pa.Correct ? "right" : "wrong")})\n");
        }

        return sb.ToString();
    }

    public static string BuildUserPrompt(AiGuessContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append("PLAYERS: ").Append(string.Join(", ", ctx.PlayerNames)).Append('\n');
        sb.Append("PROMPT: ").Append(ctx.Prompt).Append("\n\nANSWERS:\n");
        foreach (var a in ctx.Answers)
            sb.Append(a.Id).Append(": ").Append(a.Text).Append('\n');
        sb.Append("\nReturn the strict JSON mapping now.");
        return sb.ToString();
    }
}
