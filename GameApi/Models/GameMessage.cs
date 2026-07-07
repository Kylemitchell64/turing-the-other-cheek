namespace GameApi.Models;

// A single answer in a game. AuthorUserId null means the message came from the AI.
public class GameMessage
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public Game? Game { get; set; }
    public int Round { get; set; }
    public string? AuthorUserId { get; set; }
    public ApplicationUser? Author { get; set; }
    public string AuthorDisplayNameAtTime { get; set; } = default!;
    public string Text { get; set; } = default!;
    public DateTime SentAt { get; set; }
}
