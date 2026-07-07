namespace GameApi.Models;

public class GamePlayer
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public Game? Game { get; set; }
    public string UserId { get; set; } = default!;
    public ApplicationUser? User { get; set; }
    public int TokensRemaining { get; set; } = 3;
    public bool IsEliminated { get; set; }
    public int VetoerCount { get; set; }
}
