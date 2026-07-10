namespace GameApi.Models;

// A persistent group of OAuth/password players who keep coming back to the same lobby.
// Unlike a live Lobby (in-memory, ephemeral 5-char code, gone when the socket drops),
// a Crew lives in the DB: a PERSISTENT join code, a saved pack/difficulty/pace config,
// and — the point of the whole feature — a GroupProfileJson the AI learns from how this
// specific group plays. Guests can't own or join crews (they have no durable identity).
public class Crew
{
    public int Id { get; set; }

    // 3-24 chars, validated at the controller. Shown to members as "CREW: <name>".
    public string Name { get; set; } = default!;

    // The account that created the crew. Owner-only actions (disband) check this. When an
    // owner leaves, ownership transfers to the oldest remaining member.
    public string OwnerUserId { get; set; } = default!;
    public ApplicationUser? Owner { get; set; }

    // The persistent 5-char code (same unambiguous alphabet as live lobbies, but stored,
    // not minted per-game). A DIFFERENT namespace from live lobby codes — this resolves
    // via CreateCrewLobby, not JoinLobby.
    public string JoinCode { get; set; } = default!;

    // Saved lobby config: seeded onto every crew lobby and re-persisted whenever the host
    // changes options in a crew game (so the crew always "comes back to the same config").
    public string PackKey { get; set; } = "family";
    public string Difficulty { get; set; } = "normal";
    public string PaceKey { get; set; } = "standard";

    public DateTime CreatedAt { get; set; }

    // The AI's accumulated knowledge of this group: strict JSON produced after each crew
    // game (vibe, running jokes, slang, who teases whom, how they hunt the AI, ...). Null
    // until the first crew game finishes. NEVER sent to clients.
    public string? GroupProfileJson { get; set; }

    public int GamesPlayed { get; set; }

    public List<CrewMember> Members { get; } = new();
}

// Membership join row. A user belongs to a crew via one of these; the (CrewId, UserId)
// pair is unique so a double-join is a no-op.
public class CrewMember
{
    public int Id { get; set; }
    public int CrewId { get; set; }
    public Crew? Crew { get; set; }
    public string UserId { get; set; } = default!;
    public ApplicationUser? User { get; set; }
    public DateTime JoinedAt { get; set; }
}
