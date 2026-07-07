using System.Security.Cryptography;

namespace GameApi.GameLoop;

// The 60-prompt pool, transcribed verbatim from AI-DESIGN section 5. Short, casual,
// low-stakes party-game prompts so the AI can blend into the group.
public static class PromptPool
{
    public static readonly string[] Prompts =
    {
        "worst purchase you ever made",
        "describe your morning in 5 words",
        "what food will you never eat again",
        "most useless talent you have",
        "last thing you googled (be honest)",
        "your go-to shower thought",
        "worst haircut era of your life",
        "a smell that instantly takes you back",
        "the app you waste the most time on",
        "your walk-up song if you had one",
        "weirdest thing in your fridge right now",
        "a hill you will absolutely die on",
        "your most irrational fear",
        "best meal you ever had for under $10",
        "the chore you always put off",
        "a movie everyone loves that you dont get",
        "your phone battery percentage right now and why",
        "the last white lie you told",
        "dream job at age 8 vs now",
        "worst advice you ever followed",
        "a word you always misspell",
        "your comfort show you rewatch",
        "the pettiest thing that ruins your day",
        "describe your driving in 3 words",
        "what would your pet say about you",
        "your signature dish (be real)",
        "a trend you fell for hard",
        "worst date story in one sentence",
        "the superpower youd pick for lazy reasons",
        "your unpopular pizza opinion",
        "how you actually sleep at night (position)",
        "a purchase youre still defending",
        "the celebrity youd swap lives with for a day",
        "your most-used emoji and what it says about you",
        "worst subject in school and why it was math",
        "a sound that drives you insane",
        "the snack you hide from everyone",
        "your camera roll in 3 words",
        "if animals could talk which would be rudest",
        "the excuse you use way too often",
        "your karaoke song of no return",
        "a rule you made for yourself and broke immediately",
        "the last time you cried at something dumb",
        "your airport routine in 5 words",
        "whats your red flag (own it)",
        "a game you rage quit forever",
        "the drink order that defines you",
        "your worst kitchen disaster",
        "if your life had a loading screen tip what would it say",
        "the compliment you never know how to take",
        "your most controversial breakfast take",
        "a place you pretend to like",
        "the text you regret sending",
        "your energy at 9am in 3 words",
        "a thing you own way too many of",
        "the youtube rabbit hole that got you recently",
        "your funeral song (dark but go)",
        "what youd grab first in a fire after people and pets",
        "the habit you judge others for but also do",
        "your best terrible pun"
    };

    // A random prompt. Rounds pick fresh each time; repeats across a game are fine
    // and rare enough with 60 entries.
    public static string Random() => Prompts[RandomNumberGenerator.GetInt32(Prompts.Length)];

    public static int Count => Prompts.Length;
}
