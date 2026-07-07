namespace GameApi.Models;

// The prompt text shown for a given round of a game. Stored so a game's chat history
// can render "R1 PROMPT: ..." for each round without re-deriving it from anything.
public class GameRoundPrompt
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public Game? Game { get; set; }
    public int Round { get; set; }
    public string Prompt { get; set; } = default!;
}
