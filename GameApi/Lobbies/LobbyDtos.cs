using GameApi.Characters;

namespace GameApi.Lobbies;

// Payloads that go over the wire to clients. SECURITY: these must never carry
// anything that distinguishes the AI from a human — no user IDs, no author
// flags, no "isAi". Only display name + token count + coarse status + character.
//
// Character is ANONYMITY-CRITICAL: every seat always carries one. A player's saved
// custom config if they have one, otherwise the deterministic name-hash default the
// client would compute anyway — and the AI seat gets the SAME name-hash default of its
// fake name. So a fully-customized lobby still can't single the AI out by its character.
public record LobbyPlayerDto(
    string DisplayName, int TokensRemaining, bool IsConnected, bool IsHost, CharacterConfig Character);

// Full LobbyUpdated payload — includes the join code and who the host is by name
// so the client can show the host-only Start button. PackKey/Difficulty/PaceKey let
// a late joiner see the current options (all host-driven, reveal nothing about the AI).
public record LobbyStateDto(
    string Code, string State, List<LobbyPlayerDto> Players,
    string PackKey, string Difficulty, string PaceKey);

// One entry in GameStarted(roster[]). Humans + the AI, indistinguishable.
// Deliberately NO user id, NO isAi, NO isHost — just name + tokens + character.
public record RosterEntryDto(string DisplayName, int TokensRemaining, CharacterConfig Character);
