namespace GameApi.Models;

public class Game
{
    public int Id { get; set; }
    public string JoinCode { get; set; } = default!;
    public GameState State { get; set; }
    public string? WinnerUserId { get; set; }
    public WinType WinType { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    public List<GamePlayer> Players { get; set; } = new();
    public List<GameMessage> Messages { get; set; } = new();
    public List<GameRoundPrompt> RoundPrompts { get; set; } = new();
}
