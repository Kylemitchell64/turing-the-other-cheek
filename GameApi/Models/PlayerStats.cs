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
    // Reverse mode (phase 22): how many times the AI correctly attributed one of this
    // player's answers to them. Incremented per correct attribution against them.
    public int TimesReadByAi { get; set; }
}
