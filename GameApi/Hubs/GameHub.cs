using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using GameApi.GameLoop;
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
    private const int AnswerMaxLength = 280;

    private readonly LobbyStore _store;
    private readonly GameEngine _engine;
    private readonly ILogger<GameHub> _logger;

    public GameHub(LobbyStore store, GameEngine engine, ILogger<GameHub> logger)
    {
        _store = store;
        _engine = engine;
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

        // On-demand style-profile refresh before we inject summaries: if a member has
        // samples but a missing/stale profile, regenerate it now (off the lock, awaited)
        // so the AI plays with the freshest notes. Best-effort — never blocks the start.
        var memberIds = new List<string>();
        lock (lobby.Sync)
            memberIds.AddRange(lobby.Players.Select(p => p.UserId));
        await _engine.RefreshStyleProfilesAsync(memberIds);

        List<RosterEntryDto> roster;
        var outbound = new List<Func<Task>>();
        lock (lobby.Sync)
        {
            if (lobby.HostUserId != UserId)
                throw new HubException("Only the host can start the game");

            // Startable fresh from the Lobby state, or as a rematch from Ended.
            if (lobby.State != GameState.Lobby && lobby.State != GameState.Ended)
                throw new HubException("The game has already started");

            // Rematch: wipe last game's tokens/eliminations/transcript, back to Lobby.
            if (lobby.State == GameState.Ended)
                lobby.ResetForNewGame();

            var humanCount = lobby.Players.Count;
            if (humanCount < 3)
                throw new HubException("Need at least 3 players to start");
            if (humanCount > 8)
                throw new HubException("Too many players (max 8)");

            // Fresh AI name each game (including rematches), colliding with no real player.
            var aiName = _store.PickAiName(lobby.Players.Select(p => p.DisplayName));
            lobby.AiDisplayName = aiName;

            // Build roster: humans + AI, then shuffle so the AI isn't last.
            var entries = lobby.Players
                .Select(p => new RosterEntryDto(p.DisplayName, p.TokensRemaining))
                .ToList();
            entries.Add(new RosterEntryDto(aiName, 3));

            roster = ShuffleAiNotLast(entries, aiName);

            // Engine drives the round loop from here; it sets state to Prompting and
            // queues the first PromptStarted into outbound.
            _engine.BeginGame(lobby, outbound);
        }

        _logger.LogInformation("Lobby {Code} started with {Count} in roster", lobby.Code, roster.Count);
        await Clients.Group(lobby.Code).SendAsync("GameStarted", roster);
        foreach (var send in outbound)
            await send();
    }

    // Submit this player's answer for the current round. 280-char cap, one per
    // player per round, only during Prompting.
    public async Task SubmitAnswer(string text)
    {
        var lobby = FindLobbyForCaller() ?? throw new HubException("You're not in a lobby");

        text = (text ?? "").Trim();
        if (text.Length == 0)
            throw new HubException("Answer can't be empty");
        if (text.Length > AnswerMaxLength)
            text = text[..AnswerMaxLength];

        lock (lobby.Sync)
        {
            if (lobby.State != GameState.Prompting)
                throw new HubException("Not accepting answers right now");

            var me = lobby.FindPlayer(UserId)
                ?? throw new HubException("You're not in this game");

            if (lobby.Answers.ContainsKey(me.DisplayName))
                throw new HubException("You already answered this round");

            // Record into live answers + permanent transcript (persisted at game end).
            lobby.Answers[me.DisplayName] = new RoundAnswer { Text = text, IsAi = false };
            lobby.Transcript.Add(new RecordedAnswer
            {
                Round = lobby.RoundNumber,
                DisplayName = me.DisplayName,
                AuthorUserId = me.UserId,
                Text = text,
                IsAi = false,
                SentAtUtc = DateTime.UtcNow
            });
        }

        // Ack only to the caller — nothing broadcast; answers stay hidden until reveal.
        await Clients.Caller.SendAsync("AnswerAccepted", lobby.RoundNumber);
    }

    // Accuse one player of being the AI. Eligibility: has tokens, not eliminated,
    // respects the veto-cooldown priority window. First accusation locks the window.
    public async Task MakeAccusation(string accusedName)
    {
        var lobby = FindLobbyForCaller() ?? throw new HubException("You're not in a lobby");
        accusedName = (accusedName ?? "").Trim();

        var outbound = new List<Func<Task>>();
        lock (lobby.Sync)
        {
            if (lobby.State != GameState.Accusing)
                throw new HubException("The accusation window isn't open");

            if (lobby.AccuserName != null)
                throw new HubException("Someone already accused this round");

            var me = lobby.FindPlayer(UserId)
                ?? throw new HubException("You're not in this game");

            if (me.IsEliminated)
                throw new HubException("You can't accuse — you're out of tokens");
            if (me.TokensRemaining <= 0)
                throw new HubException("You have no tokens left");

            // During a vetoer's exclusive priority sub-window, only they may accuse.
            if (lobby.InPriorityWindow &&
                !string.Equals(me.DisplayName, lobby.PriorityVetoerName, StringComparison.Ordinal))
                throw new HubException("It's the priority window — wait your turn");

            // Blackout round: no accusations at all (shouldn't reach here since the
            // window never opens, but guard anyway).
            if (lobby.BlackoutRound == lobby.RoundNumber)
                throw new HubException("No accusations this round");

            // Target must be a real seat or the AI's name; can't accuse yourself.
            var isAiName = string.Equals(accusedName, lobby.AiDisplayName, StringComparison.Ordinal);
            var targetSeat = lobby.FindPlayerByName(accusedName);
            if (!isAiName && targetSeat == null)
                throw new HubException("No such player");
            if (string.Equals(accusedName, me.DisplayName, StringComparison.Ordinal))
                throw new HubException("You can't accuse yourself");

            lobby.AccuserName = me.DisplayName;
            lobby.AccusedName = accusedName;

            // Opens the veto window (only to other token-holders) + broadcasts AccusationMade.
            _engine.OpenVetoWindow(lobby, outbound);
        }

        foreach (var send in outbound)
            await send();
    }

    // Spend a token to veto the locked accusation. Only other token-holders can. The
    // accusation result is NEVER revealed; a cooldown + priority window follow.
    public async Task UseFakeOut()
    {
        var lobby = FindLobbyForCaller() ?? throw new HubException("You're not in a lobby");

        var outbound = new List<Func<Task>>();
        lock (lobby.Sync)
        {
            if (lobby.State != GameState.VetoWindow)
                throw new HubException("There's no veto window open");

            var me = lobby.FindPlayer(UserId)
                ?? throw new HubException("You're not in this game");

            if (!lobby.VetoEligible.Contains(me.DisplayName))
                throw new HubException("You can't veto this accusation");
            if (!me.CanVeto)
                throw new HubException("You can't veto — no tokens or eliminated");

            _engine.ApplyVeto(lobby, me, outbound);
        }

        foreach (var send in outbound)
            await send();
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
