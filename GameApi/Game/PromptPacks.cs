using System.Security.Cryptography;

namespace GameApi.GameLoop;

// A themed set of round prompts the host can pick before starting. FAMILY keeps the
// original 60 casual prompts; ADULT / DRINKING / TRIVIA are the phase-10 additions.
// Nothing here ever mentions the AI — the pack is host-driven and safe to broadcast.
public record PromptPack(string Key, string DisplayName, string Label, string Description, string[] Prompts);

public static class PromptPacks
{
    public const string DefaultKey = "family";

    // The reserved key for an AI-built custom pack (phase 20). NOT a member of All and
    // NOT accepted by IsValidKey — a lobby only gets it via the host's SetCustomPack, and
    // its prompts come from Lobby.CustomPack, not from any table here.
    public const string CustomKey = "custom";

    // FAMILY reuses the existing 60-prompt casual pool (AI-DESIGN section 5) so the
    // default game is unchanged. The other three are new.
    public static readonly PromptPack Family = new(
        Key: "family",
        DisplayName: "Family",
        Label: "FAMILY",
        Description: "keep it clean. party prompts anyone can answer.",
        Prompts: PromptPool.Prompts);

    // Dirty-joke party tier: innuendo, embarrassing confessions, dating/hookup
    // misadventures. Crude R-rated humor, nothing explicit or graphic.
    public static readonly PromptPack Adult = new(
        Key: "adult",
        DisplayName: "Adult",
        Label: "ADULT 18+",
        Description: "18+ // crude party humor, innuendo and confessions. keep it playful.",
        Prompts: new[]
        {
            "the last text you'd be mortified for your mom to read",
            "worst pickup line you've ever used or had used on you",
            "your most embarrassing drunk hookup story in one line",
            "the ick that instantly ends it for you",
            "worst place you've ever hooked up",
            "a dating app red flag you swipe left on instantly",
            "the most unhinged thing in your search history",
            "rate your walk of shame outfit out of 10",
            "worst thing you've done to avoid a second date",
            "the pettiest reason you ever ghosted someone",
            "the least sexy thing that somehow does it for you",
            "worst nickname a partner ever gave you",
            "a lie you told to get out of a hookup",
            "the horniest decision you made completely sober",
            "your go-to move that works way too often",
            "worst sext you've ever sent or received",
            "the most embarrassing thing you own that a date could find",
            "how many exes are still in your phone and why",
            "the wildest thing you'd do for the right person",
            "your most shameful 2am text recipient",
            "worst thing you've faked to spare someone's feelings",
            "the thing you'll admit to only in vague terms",
            "a hookup that ended in pure disaster, go",
            "the thirstiest thing you've done on the internet",
            "worst thing a partner ever caught you doing",
            "your dealbreaker that's honestly kind of shallow",
            "the most desperate you've ever been on a dating app",
            "a confession that would ruin your reputation in this room",
            "the most embarrassing noise you've ever made in the moment",
            "your type in 3 words (be honest and a little gross)",
            "the dumbest thing you've done to impress a crush",
            "worst wingman fail you witnessed or committed",
            "the weird thing you're unexpectedly into",
            "your most regrettable ex in one word",
            "worst thing you've said mid-hookup by accident",
            "the group chat secret you swore you'd never tell",
            "how you actually rate yourself in bed (be delusional)",
            "the flirting attempt that crashed and burned hardest",
            "worst place you've ever been caught making out",
            "the most on-brand reason you got left on read",
        });

    // "Everyone who X drinks", never-have-I-ever, dares tied to answers. Description
    // carries the standing responsible-drinking line.
    public static readonly PromptPack Drinking = new(
        Key: "drinking",
        DisplayName: "Drinking",
        Label: "DRINKING 21+",
        Description: "21+ // never-have-i-ever and dares. drink responsibly, know your limits, never drink and drive.",
        Prompts: new[]
        {
            "never have i ever blacked out and lost my phone (story or drink)",
            "everyone who's texted an ex after midnight drinks - whats the text",
            "the drunkest decision you ever fully committed to",
            "never have i ever thrown up in an uber (confess or drink)",
            "everyone who's been cut off by a bartender drinks - why",
            "your most reliable drunk food order",
            "never have i ever forgotten someone's name mid conversation (drink)",
            "the worst hangover you've earned and how",
            "everyone who's cried at a bar drinks - what set you off",
            "your party trick that only works after a few",
            "never have i ever woken up somewhere i didnt recognize",
            "the dare you'd actually do right now for a free drink",
            "everyone who's been the drunkest in the room drinks - own it",
            "your go-to shot and the memory attached to it",
            "never have i ever sent a text i couldnt take back (drink)",
            "the most money you've spent on a night you barely remember",
            "everyone who's fallen asleep at a party drinks - where",
            "your worst 'im never drinking again' moment",
            "never have i ever made a 2am purchase i regret (drink)",
            "the drink that ruined you and you'll never touch again",
            "everyone who's texted the group chat something unhinged drinks",
            "the dumbest thing you've done to win a bet at a bar",
            "never have i ever started a tab i regret (drink)",
            "your karaoke pick after exactly 4 drinks",
            "the loudest secret you've spilled while drunk",
            "everyone who's been carried home drinks - by who",
            "your most confident drunk plan that made no sense",
            "never have i ever fought over the aux drunk (drink)",
            "the worst wingman moment you caused after a few",
            "name a dare for the person to your left",
            "everyone who's woken up still in their shoes drinks",
            "your drunk-texting hall of fame recipient",
            "never have i ever snuck a drink somewhere i shouldnt",
            "the pettiest drunk argument you've ever had",
            "your most 'that was a mistake' morning-after moment",
            "everyone who's lost a shoe on a night out drinks",
            "the dare that ended a party early - whats yours",
            "never have i ever faked being sober for someone (drink)",
            "your bar tab horror story in one line",
            "everyone who's closed down a bar drinks - last one out?",
        });

    // Obscure-ish general knowledge where most people are guessing. Phrased so
    // confident bullshitting feels natural.
    public static readonly PromptPack Trivia = new(
        Key: "trivia",
        DisplayName: "Trivia",
        Label: "TRIVIA",
        Description: "obscure general knowledge. no googling, commit to your guess.",
        Prompts: new[]
        {
            "whats the capital of kazakhstan (no googling, commit to your guess)",
            "how many bones are in the human body (confident number only)",
            "what year did the berlin wall fall (guess, dont overthink it)",
            "name the longest river in the world (first answer that pops up)",
            "what element has the symbol W (just say something)",
            "how many countries are in africa (rough number, commit)",
            "who painted the girl with a pearl earring (no cheating)",
            "which planet is the hottest in the solar system (obvious right?)",
            "whats the smallest country in the world (guess confidently)",
            "how tall is mount everest in feet (ballpark it)",
            "what language has the most native speakers (commit)",
            "who was first to reach the south pole (a name, any name)",
            "what year did the first iphone come out (guess)",
            "whats the currency of thailand (say it like you know)",
            "how many hearts does an octopus have (number, go)",
            "what does the www in a web address stand for (all three words)",
            "which planet has the most moons (pick one and commit)",
            "whats the largest desert in the world (careful, its a trick)",
            "how many time zones does russia have (guess)",
            "who wrote the odyssey (name drop confidently)",
            "what year did the titanic sink (commit to a year)",
            "whats the hardest natural substance on earth (obvious? maybe)",
            "name the capital of australia (bet you get it wrong)",
            "how many keys are on a standard piano (exact number, guess)",
            "what gas do plants absorb from the air (basic but commit)",
            "whats the most abundant element in the universe (go)",
            "who is credited with inventing the telephone (a name, quickly)",
            "how far is the moon from earth in miles (ballpark)",
            "which country has the most pyramids (its a trick)",
            "whats the national animal of scotland (this one's absurd, guess anyway)",
            "how many players are on a cricket team (number)",
            "what year did world war one start (commit)",
            "whats the deepest ocean trench called (say something)",
            "which vitamin do you get from sunlight (easy? prove it)",
            "who discovered penicillin (name it fast)",
            "whats the capital of canada (not the obvious one)",
            "how many chambers are in the human heart (number, go)",
            "whats the fastest land animal (confident answer)",
            "how many moons does mars have (exact number, guess)",
            "whats the tallest animal in the world (dont overthink it)",
        });

    // Earnest-but-light icebreakers, would-you-rathers with a reason, and nostalgia.
    // Work-safe and sincere — the pack for a room that wants to actually talk.
    public static readonly PromptPack Deep = new(
        Key: "deep",
        DisplayName: "Deep Cuts",
        Label: "DEEP CUTS",
        Description: "earnest icebreakers, would-you-rathers, and nostalgia. get a little real.",
        Prompts: new[]
        {
            "the song that instantly takes you back to being 15",
            "would you rather always be 10 min early or 20 min late, and why",
            "the small thing that made you ridiculously happy this week",
            "a hobby you'd pick up if money and time didn't matter",
            "the show you'd still drop everything to rewatch",
            "would you rather read minds or be invisible, pick and defend it",
            "the meal that tastes like your childhood",
            "a compliment you got once that you still think about",
            "the thing you were weirdly obsessed with as a kid",
            "would you rather never lose your keys or never lose your phone, why",
            "the place you felt most at peace, describe it in one line",
            "a smell that instantly brings back a specific memory",
            "the teacher who actually changed something for you",
            "would you rather have more time or more money, be honest",
            "the toy or game you'd beg your parents for",
            "a tiny ritual that makes your day feel right",
            "the movie you quote way too often",
            "would you rather relive one day or skip to a future one, which",
            "the friend you keep meaning to text back",
            "a skill you're low-key proud of that never comes up",
            "the snack that defined your after-school years",
            "would you rather always know the truth or stay blissfully unaware",
            "the book or story that stuck with you for years",
            "a place you've never been but really want to see",
            "the family saying nobody outside your house would get",
            "would you rather be famous for a day or comfortable forever, why",
            "the thing you'd tell your 12 year old self",
            "a moment you realized you were actually growing up",
            "the game you played till the streetlights came on",
            "would you rather have a rewind button or a pause button, pick",
            "the person who taught you something without meaning to",
            "a comfort show you put on just for the background noise",
            "the holiday tradition you secretly love",
            "would you rather live by the ocean or in the mountains, why",
            "the hobby you quit but kind of miss",
            "a sound that instantly relaxes you",
            "the thing you're most looking forward to lately",
            "would you rather redo your best day or erase your worst, which",
            "the old friend you'd grab coffee with tomorrow if you could",
            "a memory you'd keep if you could only keep one",
        });

    // Work-safe office party: meetings, emails, wfh, printer rage. The pack for a team
    // offsite or a slack crew — nothing HR would flag.
    public static readonly PromptPack Office = new(
        Key: "office",
        DisplayName: "Water Cooler",
        Label: "WATER COOLER",
        Description: "work-safe office party. meetings, emails, wfh chaos, printer rage.",
        Prompts: new[]
        {
            "the meeting that absolutely should have been an email",
            "your most-used passive aggressive email phrase",
            "the break room snack you'd riot over if it disappeared",
            "how many tabs do you have open right now, be honest",
            "the exact moment you mentally check out on a friday",
            "your go-to excuse for turning your camera off",
            "the printer has wronged you — tell us how",
            "worst buzzword you've heard in a meeting this year",
            "your fake-busy move when the boss walks by",
            "the reply-all disaster you witnessed or caused",
            "what your 'quick sync' actually turns into",
            "the wfh outfit you'd never admit to on camera",
            "your most unhinged slack status",
            "the task you've been 'circling back' to for a month",
            "what you're really doing during a boring standup",
            "the office kitchen crime that made you lose faith in people",
            "your honest answer to 'how's it going' at 9am",
            "the email you rewrote five times before sending",
            "worst place someone put you on speaker phone",
            "your realest reason for booking a conference room",
            "the calendar invite that ruined your afternoon",
            "how long your 'be right back' really means",
            "the coworker habit that lives in your head rent free",
            "your move when someone schedules a meeting over lunch",
            "the thing you pretend to understand in every meeting",
            "worst team-building activity you've been forced into",
            "your commute story that still haunts you",
            "the slack channel you muted immediately",
            "what 'let's take this offline' actually means to you",
            "the office chair or desk hill you'll die on",
            "your most creative use of 'per my last email'",
            "the notification that spikes your blood pressure instantly",
            "how you really feel about the mandatory fun day",
            "the coffee order that says everything about a coworker",
            "your escape plan when a meeting runs over",
            "the spreadsheet that nearly broke you",
            "what you'd put in the suggestion box anonymously",
            "your realest thought during a 'let's circle back next week'",
            "the desk snack stash you guard with your life",
            "the one perk that would actually keep you here",
        });

    // Selector order (Kyle's explicit ask): SFW packs first, the dirty ones LAST.
    // family, deep, office, trivia, adult, drinking. The client PACKS array mirrors this
    // and PromptPackParityTests locks the two lists so they can't drift.
    public static readonly IReadOnlyList<PromptPack> All = new[] { Family, Deep, Office, Trivia, Adult, Drinking };

    private static readonly Dictionary<string, PromptPack> ByKey =
        All.ToDictionary(p => p.Key, StringComparer.Ordinal);

    public static bool IsValidKey(string? key) => key != null && ByKey.ContainsKey(key);

    // Look up a pack, falling back to FAMILY for an unknown/null key so a bad value
    // can never crash a round.
    public static PromptPack Get(string? key) =>
        key != null && ByKey.TryGetValue(key, out var pack) ? pack : Family;

    // Pick a prompt from the pack that hasn't been used yet this game. usedIndices is
    // per-game state on the lobby; when the pack is exhausted we clear it and start
    // over (rare — packs are 40-60 deep, games are <=8 rounds).
    public static string PickPrompt(string key, HashSet<int> usedIndices) =>
        PickPrompt(Get(key).Prompts, usedIndices);

    // Same no-repeat pick over an arbitrary prompt array — used for the custom pack, whose
    // prompts live on the lobby rather than in a static PromptPack.
    public static string PickPrompt(string[] prompts, HashSet<int> usedIndices)
    {
        if (usedIndices.Count >= prompts.Length)
            usedIndices.Clear();

        int idx;
        do
        {
            idx = RandomNumberGenerator.GetInt32(prompts.Length);
        }
        while (usedIndices.Contains(idx));

        usedIndices.Add(idx);
        return prompts[idx];
    }
}
