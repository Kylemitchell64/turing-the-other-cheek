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
    AiSurvival
}
