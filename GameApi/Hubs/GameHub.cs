using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using GameApi.Lobbies;
using GameApi.Models;

namespace GameApi.Hubs;

// The realtime hub. Every method is JWT-authed (token comes off the query string,
// see Program.cs OnMessageReceived). Every mutation takes the target lobby's lock
// and validates the caller belongs to the lobby and the action is legal for the
// current state — the client is never trusted.
[Authorize]
public class GameHub : Hub
{
    private readonly LobbyStore _store;
    private readonly ILogger<GameHub> _logger;

    public GameHub(LobbyStore store, ILogger<GameHub> logger)
    {
        _store = store;
        _logger = logger;
    }

    private string UserId =>
        Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new HubException("Not authenticated");

    private string DisplayName =>
        Context.User?.FindFirstValue("displayName")
        ?? Context.User?.FindFirstValue(ClaimTypes.Name)
        ?? "player";

    // Host opens a new lobby and is seated as the first player.
    public async Task CreateLobby()
    {
        var lobby = _store.Create(UserId);

        lock (lobby.Sync)
        {
            var player = new LobbyPlayer { UserId = UserId, DisplayName = DisplayName };
            player.ConnectionIds.Add(Context.ConnectionId);
            lobby.Players.Add(player);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, lobby.Code);
        _logger.LogInformation("Lobby {Code} created by {User}", lobby.Code, UserId);
        await BroadcastLobby(lobby);
    }

    // Join an existing lobby by code. Reconnects (same userId) fold back into the
    // existing seat rather than creating a duplicate.
    public async Task JoinLobby(string code)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        var lobby = _store.Get(code) ?? throw new HubException("No lobby with that code");

        lock (lobby.Sync)
        {
            if (lobby.State != GameState.Lobby)
                throw new HubException("That game has already started");

            if (lobby.Players.Count >= 8 && lobby.FindPlayer(UserId) == null)
                throw new HubException("That lobby is full");

            var existing = lobby.FindPlayer(UserId);
            if (existing != null)
            {
                existing.ConnectionIds.Add(Context.ConnectionId);
            }
            else
            {
                var player = new LobbyPlayer { UserId = UserId, DisplayName = DisplayName };
                player.ConnectionIds.Add(Context.ConnectionId);
                lobby.Players.Add(player);
            }
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, lobby.Code);
        _logger.LogInformation("{User} joined lobby {Code}", UserId, lobby.Code);
        await BroadcastLobby(lobby);
    }

    // Host-only. Requires 3–8 humans. Injects the AI and shuffles the roster so
    // the AI is never last, then announces GameStarted.
    public async Task StartGame()
    {
        // Find which lobby this caller hosts by scanning their connection group is
        // awkward; instead we require the caller to be in exactly one lobby. Look
        // it up from their connection.
        var lobby = FindLobbyForCaller() ?? throw new HubException("You're not in a lobby");

        List<RosterEntryDto> roster;
        lock (lobby.Sync)
        {
            if (lobby.HostUserId != UserId)
                throw new HubException("Only the host can start the game");

            if (lobby.State != GameState.Lobby)
                throw new HubException("The game has already started");

            var humanCount = lobby.Players.Count;
            if (humanCount < 3)
                throw new HubException("Need at least 3 players to start");
            if (humanCount > 8)
                throw new HubException("Too many players (max 8)");

            // Inject the AI under a name that collides with no real player.
            var aiName = _store.PickAiName(lobby.Players.Select(p => p.DisplayName));
            lobby.AiDisplayName = aiName;

            // Build roster: humans + AI, then shuffle so the AI isn't last.
            var entries = lobby.Players
                .Select(p => new RosterEntryDto(p.DisplayName, p.TokensRemaining))
                .ToList();
            entries.Add(new RosterEntryDto(aiName, 3));

            roster = ShuffleAiNotLast(entries, aiName);

            lobby.State = GameState.Prompting;
        }

        _logger.LogInformation("Lobby {Code} started with {Count} in roster", lobby.Code, roster.Count);
        await Clients.Group(lobby.Code).SendAsync("GameStarted", roster);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Drop this connection from whatever seat holds it. The seat itself stays
        // (phone lock / refresh reconnects by userId). If a lobby empties entirely
        // while still pre-game, discard it.
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId != null)
        {
            var lobby = FindLobbyForCaller();
            if (lobby != null)
            {
                var shouldDrop = false;
                lock (lobby.Sync)
                {
                    var player = lobby.FindPlayer(userId);
                    player?.ConnectionIds.Remove(Context.ConnectionId);

                    if (lobby.State == GameState.Lobby && lobby.Players.All(p => !p.IsConnected))
                        shouldDrop = true;
                }

                if (shouldDrop)
                    _store.Remove(lobby.Code);
                else
                    await BroadcastLobby(lobby);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    // --- helpers ---

    private async Task BroadcastLobby(Lobby lobby)
    {
        LobbyStateDto dto;
        lock (lobby.Sync)
        {
            var players = lobby.Players
                .Select(p => new LobbyPlayerDto(
                    p.DisplayName, p.TokensRemaining, p.IsConnected, p.UserId == lobby.HostUserId))
                .ToList();
            dto = new LobbyStateDto(lobby.Code, lobby.State.ToString(), players);
        }
        await Clients.Group(lobby.Code).SendAsync("LobbyUpdated", dto);
    }

    // Locate the lobby the current connection belongs to. Small linear scan —
    // lobby counts are tiny (a handful at a time on a free tier).
    private Lobby? FindLobbyForCaller()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return null;

        foreach (var lobby in _store.All)
        {
            lock (lobby.Sync)
            {
                var player = lobby.FindPlayer(userId);
                if (player != null && player.ConnectionIds.Contains(Context.ConnectionId))
                    return lobby;
            }
        }
        return null;
    }

    // Shuffle, but guarantee the AI never lands in the last slot.
    private static List<RosterEntryDto> ShuffleAiNotLast(List<RosterEntryDto> entries, string aiName)
    {
        var list = entries.ToList();
        do
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
        while (list[^1].DisplayName == aiName);
        return list;
    }
}
