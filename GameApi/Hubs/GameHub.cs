using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using GameApi.Admin;
using GameApi.Characters;
using GameApi.Data;
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
    private readonly GameContext _db;
    private readonly MaintenanceState _maintenance;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PackCodec _packCodec;
    private readonly ILogger<GameHub> _logger;

    public GameHub(LobbyStore store, GameEngine engine, GameContext db, MaintenanceState maintenance,
        IServiceScopeFactory scopeFactory, PackCodec packCodec, ILogger<GameHub> logger)
    {
        _store = store;
        _engine = engine;
        _db = db;
        _maintenance = maintenance;
        _scopeFactory = scopeFactory;
        _packCodec = packCodec;
        _logger = logger;
    }

    // Maintenance pause: new lobbies + game starts are refused with the operator's message.
    // Running games are untouched so nobody gets dropped mid-round.
    private void ThrowIfMaintenance()
    {
        var (on, message) = _maintenance.Snapshot();
        if (on)
            throw new HubException(string.IsNullOrWhiteSpace(message)
                ? "the game is paused for maintenance, check back soon"
                : message);
    }

    private string UserId =>
        Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new HubException("Not authenticated");

    private string DisplayName =>
        Context.User?.FindFirstValue("displayName")
        ?? Context.User?.FindFirstValue(ClaimTypes.Name)
        ?? "player";

    private bool IsGuestCaller =>
        string.Equals(Context.User?.FindFirstValue("isGuest"), "true", StringComparison.OrdinalIgnoreCase);

    // Host opens a new lobby and is seated as the first player.
    public async Task CreateLobby()
    {
        ThrowIfMaintenance();
        var lobby = _store.Create(UserId);
        var characterJson = await LoadCharacterJsonAsync(UserId);

        lock (lobby.Sync)
        {
            var player = new LobbyPlayer { UserId = UserId, DisplayName = DisplayName, CharacterJson = characterJson };
            player.ConnectionIds.Add(Context.ConnectionId);
            lobby.Players.Add(player);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, lobby.Code);
        _logger.LogInformation("Lobby {Code} created by {User}", lobby.Code, UserId);
        await BroadcastLobby(lobby);
    }

    // Open the live lobby for a crew (phase 19). Seeds a fresh in-memory lobby from the
    // crew's saved config and stamps it with the crew id/name so only members can join and
    // option changes persist back. If a live lobby for this crew is already open (someone
    // else already tapped in), the caller folds into it instead of spawning a duplicate.
    public async Task CreateCrewLobby(int crewId)
    {
        ThrowIfMaintenance();
        if (IsGuestCaller)
            throw new HubException("sign in to use crews");

        var crew = await _db.Crews
            .Include(c => c.Members)
            .FirstOrDefaultAsync(c => c.Id == crewId);
        if (crew == null)
            throw new HubException("that crew doesn't exist");
        if (crew.Members.All(m => m.UserId != UserId))
            throw new HubException("you're not in that crew");

        var characterJson = await LoadCharacterJsonAsync(UserId);

        var lobby = FindOpenCrewLobby(crewId);
        if (lobby == null)
        {
            lobby = _store.Create(UserId);
            lock (lobby.Sync)
            {
                lobby.CrewId = crew.Id;
                lobby.CrewName = crew.Name;
                lobby.PackKey = crew.PackKey;
                lobby.Difficulty = crew.Difficulty;
                lobby.PaceKey = crew.PaceKey;
                // Restore the crew's saved custom pack (phase 20). A code that no longer
                // verifies (e.g. JWT_KEY rotated) just falls back to the family pack.
                if (crew.PackKey == PromptPacks.CustomKey && crew.CustomPackCode != null)
                {
                    var restored = _packCodec.TryDecode(crew.CustomPackCode, out _);
                    if (restored != null)
                        lobby.CustomPack = restored;
                    else
                        lobby.PackKey = PromptPacks.DefaultKey;
                }

                var player = new LobbyPlayer { UserId = UserId, DisplayName = DisplayName, CharacterJson = characterJson };
                player.ConnectionIds.Add(Context.ConnectionId);
                lobby.Players.Add(player);
            }
        }
        else
        {
            lock (lobby.Sync)
            {
                if (lobby.Players.Count >= 8 && lobby.FindPlayer(UserId) == null)
                    throw new HubException("That lobby is full");

                var existing = lobby.FindPlayer(UserId);
                if (existing != null)
                {
                    existing.ConnectionIds.Add(Context.ConnectionId);
                    existing.CharacterJson = characterJson;
                }
                else
                {
                    var player = new LobbyPlayer { UserId = UserId, DisplayName = DisplayName, CharacterJson = characterJson };
                    player.ConnectionIds.Add(Context.ConnectionId);
                    lobby.Players.Add(player);
                }
            }
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, lobby.Code);
        _logger.LogInformation("Crew {CrewId} lobby {Code} entered by {User}", crewId, lobby.Code, UserId);
        await BroadcastLobby(lobby);
    }

    // The still-open (pre-start) live lobby for this crew, if any.
    private Lobby? FindOpenCrewLobby(int crewId)
    {
        foreach (var lobby in _store.All)
        {
            lock (lobby.Sync)
            {
                if (lobby.CrewId == crewId && lobby.State == GameState.Lobby)
                    return lobby;
            }
        }
        return null;
    }

    // Join an existing lobby by code. Reconnects (same userId) fold back into the
    // existing seat rather than creating a duplicate.
    public async Task JoinLobby(string code)
    {
        ThrowIfMaintenance();
        code = (code ?? "").Trim().ToUpperInvariant();
        var lobby = _store.Get(code);
        if (lobby == null)
        {
            // Crew codes live in a DIFFERENT namespace (they resolve via CreateCrewLobby).
            // If the typed code is a crew's persistent code, point the player at the right door.
            if (await _db.Crews.AnyAsync(c => c.JoinCode == code))
                throw new HubException("that's a crew code — open it from your crews list");
            throw new HubException("No lobby with that code");
        }

        // A crew lobby is members-only: non-members get bounced with a clear message.
        int? crewId;
        lock (lobby.Sync) crewId = lobby.CrewId;
        if (crewId != null &&
            !await _db.CrewMembers.AnyAsync(m => m.CrewId == crewId.Value && m.UserId == UserId))
            throw new HubException("that's a crew game — only crew members can join");

        var characterJson = await LoadCharacterJsonAsync(UserId);

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
                existing.CharacterJson = characterJson; // pick up any edits since they last joined
            }
            else
            {
                var player = new LobbyPlayer { UserId = UserId, DisplayName = DisplayName, CharacterJson = characterJson };
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
        ThrowIfMaintenance();

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

        // Cheat-card rewards (phase 18): read each member's oldest unconsumed cheat card
        // now (read-only). Under the lock below we bump those players to a 4th fake-out
        // token this game; the reward is stamped consumed only after the start succeeds, so
        // a start that throws its validations never burns anybody's card.
        var cheatCards = await LoadOldestCheatCardsAsync(memberIds);

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

            // Apply cheat cards: a holder seats with 4 tokens this game instead of 3. Done
            // after any rematch reset (which set everyone back to 3) so it always sticks.
            foreach (var p in lobby.Players)
                if (cheatCards.ContainsKey(p.UserId))
                    p.TokensRemaining = 4;

            // Fresh AI name each game (including rematches), colliding with no real player.
            var aiName = _store.PickAiName(lobby.Players.Select(p => p.DisplayName));
            lobby.AiDisplayName = aiName;

            // Build roster: humans + AI, then shuffle so the AI isn't last. Every seat
            // carries a character — a human's saved config, else their name-hash default;
            // the AI gets the same name-hash default of its fake name, so it's
            // indistinguishable even when everyone else has fully customized.
            var entries = lobby.Players
                .Select(p => new RosterEntryDto(
                    p.DisplayName, p.TokensRemaining, CharacterDefaults.Resolve(p.CharacterJson, p.DisplayName)))
                .ToList();
            entries.Add(new RosterEntryDto(aiName, 3, CharacterDefaults.FromName(aiName)));

            roster = ShuffleAiNotLast(entries, aiName);

            // Engine drives the round loop from here; it sets state to Prompting and
            // queues the first PromptStarted into outbound.
            _engine.BeginGame(lobby, outbound);
        }

        // The start succeeded — NOW burn the cheat cards that seated players with 4 tokens.
        await ConsumeCheatCardsAsync(cheatCards);

        _logger.LogInformation("Lobby {Code} started with {Count} in roster", lobby.Code, roster.Count);
        await Clients.Group(lobby.Code).SendAsync("GameStarted", roster);
        foreach (var send in outbound)
            await send();
    }

    // Oldest unconsumed cheat card per member (userId -> reward id). Read-only; the
    // caller stamps them consumed only after the start actually succeeds.
    private async Task<Dictionary<string, int>> LoadOldestCheatCardsAsync(List<string> memberIds)
    {
        try
        {
            var cards = await _db.UserRewards
                .Where(r => memberIds.Contains(r.UserId) && r.Kind == "cheat_card" && r.ConsumedAt == null)
                .GroupBy(r => r.UserId)
                .Select(g => new { UserId = g.Key, Id = g.OrderBy(r => r.GrantedAt).ThenBy(r => r.Id).First().Id })
                .ToListAsync();
            return cards.ToDictionary(c => c.UserId, c => c.Id);
        }
        catch (Exception ex)
        {
            // A dead DB must never block a game start — cheat cards just don't apply.
            _logger.LogWarning(ex, "cheat card lookup failed, starting without them");
            return new Dictionary<string, int>();
        }
    }

    private async Task ConsumeCheatCardsAsync(Dictionary<string, int> cards)
    {
        if (cards.Count == 0) return;
        try
        {
            var ids = cards.Values.ToList();
            var rewards = await _db.UserRewards.Where(r => ids.Contains(r.Id)).ToListAsync();
            foreach (var r in rewards) r.ConsumedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Worst case a card survives to another game — better than failing the start.
            _logger.LogWarning(ex, "cheat card consume failed");
        }
    }

    // Host-only, pre-start only: pick the prompt pack, impostor difficulty, and answer
    // pace for this lobby. Broadcasts LobbyOptionsChanged so every client updates its
    // selectors live. All three keys are host-driven — nothing about them hints at the AI.
    public async Task SetLobbyOptions(string packKey, string difficulty, string paceKey)
    {
        var lobby = FindLobbyForCaller() ?? throw new HubException("You're not in a lobby");
        packKey = (packKey ?? "").Trim();
        difficulty = (difficulty ?? "").Trim();
        paceKey = (paceKey ?? "").Trim();

        lock (lobby.Sync)
        {
            if (lobby.HostUserId != UserId)
                throw new HubException("Only the host can change lobby options");
            if (lobby.State != GameState.Lobby)
                throw new HubException("Can't change options once the game has started");
            if (!PromptPacks.IsValidKey(packKey))
                throw new HubException("Unknown pack");
            if (!DifficultyProfile.IsValidKey(difficulty))
                throw new HubException("Unknown difficulty");
            if (!PaceOptions.IsValidKey(paceKey))
                throw new HubException("Unknown pace");

            lobby.PackKey = packKey;
            lobby.Difficulty = difficulty;
            lobby.PaceKey = paceKey;
            // Picking a normal pack clears any AI-built custom pack (and its saved code).
            lobby.CustomPack = null;
        }

        // Crew lobby: persist the new config back to the Crew row so the group always
        // "comes back to the same config". Fire-and-forget, off the lobby lock. Choosing a
        // normal pack also wipes the crew's saved custom-pack code.
        int? crewId;
        lock (lobby.Sync) crewId = lobby.CrewId;
        if (crewId != null)
            PersistCrewOptions(crewId.Value, packKey, difficulty, paceKey, customPackCode: null);

        _logger.LogInformation("Lobby {Code} options set to {Pack}/{Difficulty}/{Pace}",
            lobby.Code, packKey, difficulty, paceKey);
        await Clients.Group(lobby.Code).SendAsync("LobbyOptionsChanged", packKey, difficulty, paceKey, null);
    }

    // Host-only, pre-start only: install an AI-built custom pack from its signed share-code
    // (phase 20). The code is decoded + signature-verified SERVER-SIDE — a hand-crafted or
    // tampered code is rejected, which is what keeps the generation-time safety filter from
    // being bypassed. Sets PackKey="custom" and stashes the pack in memory on the lobby.
    public async Task SetCustomPack(string code)
    {
        var lobby = FindLobbyForCaller() ?? throw new HubException("You're not in a lobby");

        var pack = _packCodec.TryDecode(code, out var error);
        if (pack == null)
            throw new HubException(error);

        lock (lobby.Sync)
        {
            if (lobby.HostUserId != UserId)
                throw new HubException("Only the host can change lobby options");
            if (lobby.State != GameState.Lobby)
                throw new HubException("Can't change options once the game has started");

            lobby.PackKey = PromptPacks.CustomKey;
            lobby.CustomPack = pack;
            lobby.UsedPromptIndices.Clear();
        }

        // Persist the code to the crew row so the crew comes back to its custom pack.
        int? crewId;
        lock (lobby.Sync) crewId = lobby.CrewId;
        if (crewId != null)
            PersistCrewOptions(crewId.Value, PromptPacks.CustomKey, lobby.Difficulty, lobby.PaceKey,
                customPackCode: _packCodec.Encode(pack));

        _logger.LogInformation("Lobby {Code} set a custom pack ({Count} prompts)",
            lobby.Code, pack.Prompts.Length);
        await Clients.Group(lobby.Code)
            .SendAsync("LobbyOptionsChanged", lobby.PackKey, lobby.Difficulty, lobby.PaceKey, pack.Name);
    }

    // Write a crew lobby's freshly-picked options back to its Crew row. Fire-and-forget on
    // a fresh scope (the hub's own DbContext is gone once the method returns); a failure
    // just means the saved config lags a game — never blocks the live option change.
    private void PersistCrewOptions(int crewId, string pack, string difficulty, string pace, string? customPackCode)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<GameContext>();
                var crew = await db.Crews.FirstOrDefaultAsync(c => c.Id == crewId);
                if (crew == null) return;
                crew.PackKey = pack;
                crew.Difficulty = difficulty;
                crew.PaceKey = pace;
                // Only a "custom" pack carries a code; any normal pack clears it.
                crew.CustomPackCode = pack == PromptPacks.CustomKey ? customPackCode : null;
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "persisting crew {CrewId} options failed", crewId);
            }
        });
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

    // Set this player's typing state. Clients fire it on answer-box focus/non-empty and
    // clear it on blur/empty, throttled to state changes. The server re-broadcasts
    // PlayerTyping(displayName, isTyping) to the lobby, but ONLY during Prompting, and
    // ONLY for a player who hasn't answered yet (an answered player's podium goes
    // neutral even if they keep typing). The AI's identical fake indicator is driven by
    // the engine — nothing here (or in the payload) distinguishes it.
    public async Task SetTyping(bool isTyping)
    {
        var lobby = FindLobbyForCaller();
        if (lobby == null) return; // no lobby / mid-transition: silently ignore

        string? name = null;
        var changed = false;
        var effective = false;
        lock (lobby.Sync)
        {
            if (lobby.State != GameState.Prompting) return; // typing only matters while prompting

            var me = lobby.FindPlayer(UserId);
            if (me == null) return;

            name = me.DisplayName;
            // An answered player never shows as typing.
            effective = isTyping && !lobby.Answers.ContainsKey(name);
            changed = effective
                ? lobby.TypingNames.Add(name)
                : lobby.TypingNames.Remove(name);
        }

        if (changed && name != null)
            await Clients.Group(lobby.Code).SendAsync("PlayerTyping", name, effective);
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

    // Best-effort read of this user's saved character JSON, cached onto their seat.
    // A DB hiccup just returns null → the roster falls back to the name-hash default,
    // so a missing DB never blocks joining a lobby.
    private async Task<string?> LoadCharacterJsonAsync(string userId)
    {
        try
        {
            return await _db.Users
                .Where(u => u.Id == userId)
                .Select(u => u.CharacterJson)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading character for {User} failed", userId);
            return null;
        }
    }

    private async Task BroadcastLobby(Lobby lobby)
    {
        LobbyStateDto dto;
        lock (lobby.Sync)
        {
            var players = lobby.Players
                .Select(p => new LobbyPlayerDto(
                    p.DisplayName, p.TokensRemaining, p.IsConnected, p.UserId == lobby.HostUserId,
                    CharacterDefaults.Resolve(p.CharacterJson, p.DisplayName)))
                .ToList();
            dto = new LobbyStateDto(lobby.Code, lobby.State.ToString(), players,
                lobby.PackKey, lobby.Difficulty, lobby.PaceKey, lobby.CrewName,
                lobby.PackKey == PromptPacks.CustomKey ? lobby.CustomPack?.Name : null);
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
