using System.Security.Cryptography;

namespace GameApi.GameLoop;

// 60 short, casual party-game prompts. Kept deliberately low-stakes and open —
// nothing that begs a "correct" answer, so the AI can blend into the group.
public static class PromptPool
{
    private static readonly string[] Prompts =
    {
        "worst purchase you ever made",
        "describe your morning in five words",
        "a food combo you'll defend to the death",
        "the last app you rage-deleted",
        "your most useless talent",
        "what you'd name a boat",
        "an unpopular opinion about pizza",
        "the weirdest thing in your fridge right now",
        "a lie you tell yourself every monday",
        "your go-to karaoke song",
        "what your last google search was",
        "a chore you avoid at all costs",
        "the pettiest reason you've ended a friendship",
        "your walk-up song if you had one",
        "something you pretend to understand",
        "the worst advice you've ever gotten",
        "a smell that instantly takes you back",
        "your toxic trait in one sentence",
        "what you'd do with an extra hour today",
        "the snack you'd never share",
        "a hill you will die on",
        "your most irrational fear",
        "the emoji you overuse",
        "what you were doing at 3am last night",
        "a trend you never understood",
        "your grocery store nemesis item",
        "the last thing that made you laugh out loud",
        "how you'd survive a zombie apocalypse",
        "a talent you wish you had",
        "your worst haircut story in one line",
        "the wifi name you'd pick to annoy neighbors",
        "something everyone loves that you secretly hate",
        "your ideal sandwich, no wrong answers",
        "what you'd put on a billboard",
        "the app you check first every morning",
        "a childhood snack that's now gone",
        "your controversial breakfast take",
        "the dumbest way you've been injured",
        "what your pet is definitely thinking",
        "a movie you'll never rewatch",
        "your most-worn item of clothing",
        "the group chat you should mute but won't",
        "what you'd bring to a desert island",
        "a phrase you say way too much",
        "your last impulse buy",
        "the chore you'd pay someone to do",
        "something you're weirdly competitive about",
        "your go-to excuse to leave early",
        "the worst gift you've received",
        "a skill that's useless but you're proud of",
        "your comfort show for the hundredth time",
        "what you'd do first if you won the lottery",
        "the road trip snack that's non-negotiable",
        "a fashion trend you refuse to try",
        "your most-used phone shortcut",
        "the thing you always forget at the store",
        "a superpower that would ruin your life",
        "your honest opinion of pineapple on things",
        "what you'd title your autobiography",
        "the last text you sent",
    };

    // A random prompt. Rounds pick fresh each time; repeats across a game are fine
    // and rare enough with 60 entries.
    public static string Random() => Prompts[RandomNumberGenerator.GetInt32(Prompts.Length)];

    public static int Count => Prompts.Length;
}
