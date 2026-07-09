namespace GameApi.Lobbies;

// Payloads that go over the wire to clients. SECURITY: these must never carry
// anything that distinguishes the AI from a human — no user IDs, no author
// flags, no "isAi". Only display name + token count + coarse status.

// One entry in LobbyUpdated(players[]). Pre-game, so the AI isn't here yet.
public record LobbyPlayerDto(string DisplayName, int TokensRemaining, bool IsConnected, bool IsHost);

// Full LobbyUpdated payload — includes the join code and who the host is by name
// so the client can show the host-only Start button. PackKey lets a late joiner see
// the currently selected prompt pack (host-driven, reveals nothing about the AI).
public record LobbyStateDto(string Code, string State, List<LobbyPlayerDto> Players, string PackKey);

// One entry in GameStarted(roster[]). Humans + the AI, indistinguishable.
// Deliberately NO user id, NO isAi, NO isHost — just name + tokens.
public record RosterEntryDto(string DisplayName, int TokensRemaining);
