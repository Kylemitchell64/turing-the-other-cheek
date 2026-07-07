namespace GameApi.Models;

// Compact style summary the AI uses to imitate a player. SummaryJson is jsonb.
public class StyleProfile
{
    public int Id { get; set; }
    public string UserId { get; set; } = default!;
    public ApplicationUser? User { get; set; }
    public string? SummaryJson { get; set; }
    public DateTime UpdatedAt { get; set; }
}
