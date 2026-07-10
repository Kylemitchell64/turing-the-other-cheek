namespace GameApi.Dtos;

// Request bodies for the crew REST API.
public class CreateCrewRequest
{
    public string Name { get; set; } = default!;
}

public class JoinCrewRequest
{
    public string Code { get; set; } = default!;
}

// What a crew looks like to a client. Carries only the crew name + saved config +
// counts — never the group profile, member ids, or anything identity-sensitive.
public record CrewDto(
    int Id,
    string Name,
    string JoinCode,
    string PackKey,
    string Difficulty,
    string PaceKey,
    int MemberCount,
    int GamesPlayed,
    bool IsOwner);
