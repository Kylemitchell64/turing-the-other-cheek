using GameApi.Models;

namespace GameApi.Lobbies;

// In-memory lobby state. NOT an EF entity — game state lives in memory until
// it's persisted to the DB at game end (phase 3+). One lock object per lobby
// guards every mutation to Players / State.
public class Lobby
{
    public string Code { get; init; } = default!;
    public string HostUserId { get; set; } = default!;
    public GameState State { get; set; } = GameState.Lobby;

    // The lock every hub method takes before touching this lobby's state.
    public object Sync { get; } = new();

    public List<LobbyPlayer> Players { get; } = new();

    // Set on StartGame. The AI's display name (already collision-checked). Never
    // sent to clients as anything special — it just sits in the roster like a human.
    public string? AiDisplayName { get; set; }

    public LobbyPlayer? FindPlayer(string userId) =>
        Players.FirstOrDefault(p => p.UserId == userId);
}

// A human seat in the lobby. The AI is NOT a LobbyPlayer — it has no userId /
// connection and only exists as a name in the roster once the game starts.
public class LobbyPlayer
{
    public string UserId { get; init; } = default!;
    public string DisplayName { get; init; } = default!;

    // A player can hold multiple connections briefly (reconnect before the old
    // socket times out). Empty set == currently disconnected.
    public HashSet<string> ConnectionIds { get; } = new();

    public int TokensRemaining { get; set; } = 3;
    public bool IsEliminated { get; set; }

    public bool IsConnected => ConnectionIds.Count > 0;
}
