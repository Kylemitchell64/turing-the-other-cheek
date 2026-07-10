namespace GameApi.Lobbies;

// The room-soundtrack moods a host can pick (phase 21). Purely COSMETIC — the mood is
// broadcast to clients so one room plays one procedural chiptune instead of six phones
// each doing their own thing. It reveals nothing about the AI and never touches gameplay.
// "off" silences the room. The actual music is generated client-side (audio/chiptune.js);
// the server only stores + relays the chosen label.
public static class MusicMoods
{
    public const string Default = "arcade";

    public static readonly string[] All =
        { "arcade", "chill", "spooky", "hype", "boss", "off" };

    public static bool IsValid(string? mood) =>
        mood != null && Array.IndexOf(All, mood) >= 0;
}
