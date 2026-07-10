namespace GameApi.Models;

public enum SampleSource
{
    Upload,
    Game
}

public enum GameState
{
    Lobby,
    Prompting,
    Revealing,
    Accusing,
    VetoWindow,
    Ended
}

public enum WinType
{
    None,
    Detector,
    AiSurvival,
    // Reverse mode (phase 22): the AI-as-guesser outcomes. AiGuesser = the AI read the
    // group (>=50% attribution accuracy across the game); HumansHidden = the humans stayed
    // hidden (AI accuracy under 50%). New int values appended so existing rows are unaffected.
    AiGuesser,
    HumansHidden
}
