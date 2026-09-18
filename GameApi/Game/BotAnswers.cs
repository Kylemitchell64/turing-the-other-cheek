namespace GameApi.GameLoop;

// Solo demo mode (phase 29): a lone visitor can't muster three friends on the spot, so
// the lobby is padded with BOT stand-ins that answer like distracted humans. They are NOT
// the impostor — the real AI still hides among them — and they never accuse or veto. Their
// lines are deliberately a different pool from MockBrain's canned fallbacks so a demo
// game doesn't read as five copies of the same voice.
public static class BotAnswers
{
    private static readonly string[] Bank =
    {
        "idk probably the obvious one",
        "ok this is weirdly hard",
        "gonna say pizza and move on",
        "my cat. next question",
        "last tuesday for sure",
        "i refuse to answer on the grounds it makes me look bad",
        "the gym membership i used twice",
        "honestly whatever my sister said",
        "lol no comment",
        "the blue one",
        "asking the real questions here",
        "coffee, always coffee",
        "i had one and now i forgot",
        "something from like 2019",
        "hot take: none of them",
        "the loud one at the party",
        "oh easy. wait no",
        "my phone charger, every single day",
        "whatever's on sale",
        "id say the second option",
        "not me thinking about this too long",
        "a haircut i still regret",
        "does a nap count",
        "the one with the sauce",
        "you all know which one",
        "hard pass on all of the above",
        "grocery store at 9pm",
        "the answer is always tacos",
        "im choosing violence: the first one",
        "my roommate would say me",
        "that concert that got rained out",
        "spreadsheets. dont ask",
        "definitely a wednesday thing",
        "same as last time i guess",
        "skip. next",
        "a bagel, if im honest",
        "the podcast i never finished",
        "cant believe you'd ask that",
        "big fan of the middle option",
        "ok but why is this so relatable",
    };

    // Pick a line not used yet this game (falls back to any line once exhausted). Light
    // random "hand-typing" noise so the same line never repeats byte-for-byte.
    public static string Pick(HashSet<string> used, Random rng)
    {
        var fresh = Bank.Where(l => !used.Contains(l)).ToList();
        if (fresh.Count == 0) { used.Clear(); fresh = Bank.ToList(); }
        var line = fresh[rng.Next(fresh.Count)];
        used.Add(line);

        var roll = rng.NextDouble();
        if (roll < 0.15) line = char.ToUpperInvariant(line[0]) + line[1..];
        else if (roll < 0.30) line += ".";
        else if (roll < 0.38) line += " lol";
        else if (roll < 0.44) line += "!!";
        return line;
    }
}
