namespace GameApi.Models;

public class PlayerStats
{
    public int Id { get; set; }
    public string UserId { get; set; } = default!;
    public ApplicationUser? User { get; set; }
    public int DetectorWins { get; set; }
    public int GamesPlayed { get; set; }
    public int TimesFooled { get; set; }
    public int AiSurvivalGamesWitnessed { get; set; }
}
