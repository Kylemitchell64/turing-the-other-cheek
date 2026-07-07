namespace GameApi.Models;

// A pasted or harvested writing sample feeding a user's style profile.
public class WritingSample
{
    public int Id { get; set; }
    public string UserId { get; set; } = default!;
    public ApplicationUser? User { get; set; }
    public string Text { get; set; } = default!;
    public SampleSource Source { get; set; }
    public DateTime CreatedAt { get; set; }
}
