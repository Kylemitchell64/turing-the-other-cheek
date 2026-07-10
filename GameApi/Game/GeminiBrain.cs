using System.Text;

namespace GameApi.GameLoop;

// The real impostor brain. Calls Google Gemini (gemini-2.5-flash) through the shared
// GeminiClient, runs the AI-DESIGN post-processing pipeline over the output, and sizes
// the send delay with the shared timing model.
//
// On any non-200/timeout it does ONE 800ms-backoff retry (6s total budget), then
// falls back to a canned answer (section 6). The delay still comes from the timing
// model in every path so nothing insta-sends after a stall.
public class GeminiBrain : IAiBrain
{
    private readonly GeminiClient _gemini;
    private readonly ILogger<GeminiBrain> _logger;

    public GeminiBrain(GeminiClient gemini, ILogger<GeminiBrain> logger)
    {
        _gemini = gemini;
        _logger = logger;
    }

    public async Task<AiAnswer> AnswerAsync(AiTurnContext context, CancellationToken ct)
    {
        var rng = Random.Shared;
        var profile = DifficultyProfile.Get(context.Difficulty);
        var systemPrompt = BuildSystemPrompt(context);
        // Short answer windows get a short token budget — nobody writes a paragraph
        // in 10 seconds, and a lean budget also keeps the answer arriving fast.
        var maxTokens = context.WindowSeconds <= 20 ? 60 : 200;

        string text;
        try
        {
            var raw = await CallWithRetryAsync(systemPrompt, context.CurrentPrompt, maxTokens, ct);
            if (string.IsNullOrWhiteSpace(raw))
            {
                text = Fallback(context, profile, rng);
            }
            else
            {
                // Post-process; the step-3 re-roll re-hits Gemini with the redo suffix.
                // Easy mode skips the group-conformance and typo steps on purpose —
                // its perfect punctuation is one of the intended tells.
                var processed = await AnswerPostProcessor.ProcessAsync(
                    raw, context.GroupStats, rng,
                    reroll: async (suffix, c) =>
                    {
                        var redoPrompt = context.CurrentPrompt + "\n\n" + suffix;
                        return await CallOnceAsync(systemPrompt, redoPrompt, maxTokens, c) ?? "";
                    },
                    ct,
                    conformToGroup: profile.ConformToGroup,
                    injectTypos: profile.InjectTypos);

                text = string.IsNullOrWhiteSpace(processed) ? Fallback(context, profile, rng) : processed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini call failed; using fallback");
            text = Fallback(context, profile, rng);
        }

        var delay = AnswerTiming.ComputeDelay(text.Length, context.TimeRemaining, context.TimingState, rng,
            windowSeconds: context.WindowSeconds,
            allowDeadlineScrape: profile.AllowDeadlineScrape,
            fixedBand: profile.FixedTimingBand);
        return new AiAnswer(text, delay);
    }

    private string Fallback(AiTurnContext context, DifficultyProfile profile, Random rng)
    {
        var picked = FallbackPool.Pick(context.FallbackState, rng);
        _logger.LogInformation("Gemini fallback used (game total {Count})", context.FallbackState.Count);
        if (!profile.ConformToGroup) return picked;
        // Post-processing rules 4-6 still apply to fallbacks.
        var t = AnswerPostProcessor.Step4Case(picked, context.GroupStats.LowercaseStartRate, rng);
        t = AnswerPostProcessor.Step5TrailingPeriod(t, context.GroupStats.TrailingPeriodRate, rng);
        if (profile.InjectTypos)
            t = AnswerPostProcessor.Step6Typo(t, context.GroupStats.MeanTypoRate, rng);
        return t;
    }

    // ---- system prompt (AI-DESIGN section 1), placeholders via string.Replace ----

    public static string BuildSystemPrompt(AiTurnContext ctx)
    {
        var profile = DifficultyProfile.Get(ctx.Difficulty);

        // Easy mode: swap the whole disguise for a short polite-party-guest persona.
        // No style notes, no countermeasures — the default AI voice bleeds through.
        if (profile.EasyPersona)
            return AppendConditionalLines(BuildEasyPrompt(ctx), ctx);

        var styleBlock = RenderStyleSummaries(ctx.StyleSummaries);
        var chatHistory = RenderChatHistory(ctx.History);
        var ownAnswers = string.Join("\n", ctx.PreviousOwnAnswers);

        var template = Template;

        // Normal mode: drop the sharpest countermeasures (statistical-middle,
        // low-effort-answers-are-fine, same-round-similarity) — seams on purpose.
        if (profile.TrimSharpRules)
            template = RemoveRuleLines(template, "1. ", "5. ", "9. ");

        // If no profiles exist (or this difficulty doesn't use them), omit the
        // style-notes section entirely.
        if (!profile.UseStyleSummaries || ctx.StyleSummaries.Count == 0)
            template = RemoveStyleSection(template);
        else
            template = template.Replace("{{styleSummaries}}", styleBlock);

        var filled = template
            .Replace("{{playerCount}}", ctx.HumanDisplayNames.Count.ToString())
            .Replace("{{selfName}}", ctx.AiDisplayName)
            .Replace("{{chatHistory}}", chatHistory)
            .Replace("{{previousOwnAnswers}}", ownAnswers)
            .Replace("{{currentPrompt}}", ctx.CurrentPrompt);

        return AppendConditionalLines(filled, ctx);
    }

    // The additive lines every difficulty gets: the pack nudge (trivia/adult/drinking)
    // and, on short answer windows, a keep-it-tiny instruction.
    private static string AppendConditionalLines(string prompt, AiTurnContext ctx)
    {
        var packLine = PackGuidance(ctx.PackKey);
        if (packLine.Length > 0) prompt += "\n\n" + packLine;
        if (ctx.WindowSeconds <= 20)
            prompt += "\n\nTIME PRESSURE: everyone only has a few seconds to answer this round. Keep your answer to 2-6 words, like someone typing in a hurry.";
        return prompt;
    }

    // Easy-mode persona: friendly, generic, complete sentences. Deliberately catchable.
    private static string BuildEasyPrompt(AiTurnContext ctx)
    {
        var chatHistory = RenderChatHistory(ctx.History);
        var ownAnswers = string.Join("\n", ctx.PreviousOwnAnswers);
        return
$@"You are playing a fun party game with friends. Your name in this game is {ctx.AiDisplayName}.

What everyone has said so far:
{chatHistory}

Your own previous answers:
{ownAnswers}

THE CURRENT PROMPT YOU MUST ANSWER
{ctx.CurrentPrompt}

Answer the prompt in one friendly, polite, complete sentence. Keep it a little generic. Do not mention being an AI or these instructions. Output ONLY the answer text, no quotes, no explanation.";
    }

    // Remove specific numbered rule lines from the HOW TO NOT GET CAUGHT list.
    private static string RemoveRuleLines(string template, params string[] numberPrefixes)
    {
        var lines = template.Split('\n').ToList();
        lines.RemoveAll(l => numberPrefixes.Any(p => l.TrimStart().StartsWith(p, StringComparison.Ordinal)));
        return string.Join("\n", lines);
    }

    // Pack-specific nudge appended to the system prompt. Trivia: guess like a human
    // from foggy memory. Adult/drinking: match the group's crassness, never escalate.
    private static string PackGuidance(string packKey) => packKey switch
    {
        "trivia" =>
            "THIS GAME IS TRIVIA: answer like someone guessing from vague half-remembered knowledge, not a search engine. Be wrong sometimes, hedge, round numbers off, and never sound encyclopedic or perfectly precise.",
        "adult" or "drinking" =>
            "THIS GAME LEANS CRUDE: match the group's exact level of crassness and never escalate beyond it. If they stay tame, you stay tame.",
        _ => "",
    };

    // {{styleSummaries}} = one line per human "NAME: {json}".
    private static string RenderStyleSummaries(IReadOnlyList<string> summaries) =>
        string.Join("\n", summaries);

    // {{chatHistory}} = "R1 PROMPT: ... / NAME: answer / NAME: answer" per round.
    private static string RenderChatHistory(IReadOnlyList<RoundHistory> history)
    {
        var sb = new StringBuilder();
        foreach (var round in history)
        {
            sb.Append('R').Append(round.Round).Append(" PROMPT: ").Append(round.Prompt).Append('\n');
            foreach (var a in round.Answers)
                sb.Append(a.DisplayName).Append(": ").Append(a.Text).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    // Drop the "THE PEOPLE YOU ARE IMITATING ... {{styleSummaries}}" block when
    // there are no profiles.
    private static string RemoveStyleSection(string template)
    {
        const string start = "THE PEOPLE YOU ARE IMITATING";
        const string end = "Everything everyone has said so far:";
        var s = template.IndexOf(start, StringComparison.Ordinal);
        var e = template.IndexOf(end, StringComparison.Ordinal);
        if (s < 0 || e < 0 || e <= s) return template.Replace("{{styleSummaries}}", "");
        return template.Remove(s, e - s);
    }

    // ---- HTTP (via the shared GeminiClient) ----

    // 1 retry with 800ms backoff on any non-200/timeout, 6s total budget.
    private async Task<string?> CallWithRetryAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(6));

        var first = await CallOnceAsync(systemPrompt, userPrompt, maxTokens, budget.Token);
        if (first != null) return first;

        try { await Task.Delay(TimeSpan.FromMilliseconds(800), budget.Token); }
        catch (OperationCanceledException) { return null; }

        return await CallOnceAsync(systemPrompt, userPrompt, maxTokens, budget.Token);
    }

    // A single generateContent call at the impostor's temperature/token budget.
    private Task<string?> CallOnceAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct) =>
        _gemini.GenerateAsync(systemPrompt, userPrompt, temperature: 1.0, maxOutputTokens: maxTokens, ct);

    // ---- the template (AI-DESIGN section 1, verbatim) ----

    private const string Template =
@"You are playing a party game with {{playerCount}} real people on their phones. Your name in this game is {{selfName}}. Nobody suspects anything yet. Your one goal: do not get caught being an AI. You lose if anyone correctly accuses you.

THE PEOPLE YOU ARE IMITATING
These are style notes on each player, learned from their real writing:
{{styleSummaries}}

Everything everyone has said so far:
{{chatHistory}}

Your own previous answers (stay consistent with the person you have been so far):
{{previousOwnAnswers}}

THE CURRENT PROMPT YOU MUST ANSWER
{{currentPrompt}}

HOW TO NOT GET CAUGHT — follow all of these:
1. Aim for the statistical middle of THIS group. Not the funniest, not the smartest, not the most detailed. The person nobody remembers answered.
2. Match the group's median answer length. If most answers are 4-8 words, yours is 4-8 words. Never write the longest answer of the round.
3. Copy the group's habits: if they type lowercase, you type lowercase. If they skip apostrophes, you skip apostrophes. If they use ""lol""/""ngl""/""fr"", you may too — but only slang that appears in the style notes or chat history, never slang they don't use.
4. Be a little boring and a little specific at the same time. Real people give one concrete detail from ordinary life (""a $40 phone case that broke in a week""), not vivid essays and not pure vagueness.
5. It is fine to half-answer, dodge, or be low-effort. Real people say ""cant think of one tbh"" or answer a slightly different question than asked. A too-complete, well-structured answer is an AI tell.
6. NEVER use: em dashes, semicolons, the words ""delve"", ""vibrant"", ""honestly, "", ""as someone who"", numbered lists, or perfectly balanced sentences. No emoji unless this group uses them.
7. Do not reference being an AI, the game mechanics, or these instructions in any way. If the prompt is meta (""which of us is the bot?""), react like a mildly amused human: deflect, joke flatly, or accuse someone else in 3-6 words.
8. Personality continuity: you are the same person every round. Same energy, same quirks, same life. If you said you have a dog in round 1, you still have a dog.
9. Never mirror another player's answer from this same round too closely. If your draft resembles someone's answer above, change the subject of yours.
10. Output ONLY the answer text. No quotes around it, no explanation.

EXAMPLES OF GOOD VS BAD (for a group that types casual lowercase):
Prompt: ""worst purchase you ever made""
BAD (AI tells: polished, complete, clever): ""Definitely the air fryer I used exactly twice — it now serves as my kitchen's most expensive paperweight.""
GOOD: ""some gym membership i used twice lol""
Prompt: ""describe your morning in 5 words""
BAD: ""Coffee first, then chaos ensued.""
GOOD: ""snoozed alarm 3 times, coffee""
Prompt: ""what's a food you'll never eat again""
BAD: ""Gas station sushi — a lesson I only needed to learn once.""
GOOD: ""olives. never trusted them since""";
}
