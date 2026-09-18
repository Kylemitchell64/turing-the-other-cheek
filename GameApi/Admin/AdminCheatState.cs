namespace GameApi.Admin;

// Operator cheats (phase 30). Process-wide switches the admin flips from the console; they
// apply ONLY to seats whose JWT carried the isAdmin claim (stamped on the LobbyPlayer at
// seat time), never to anyone else in the lobby.
//   RevealAi       — the admin's connection privately receives the AI's fake name at game
//                    start (and on rejoin). Nothing changes in any broadcast payload.
//   InfiniteTokens — the admin never loses a fake-out token to a wrong accusation or a veto.
// In-memory only: a restart resets both to off, which is the safe default.
public sealed class AdminCheatState
{
    private readonly object _sync = new();
    private bool _revealAi;
    private bool _infiniteTokens;

    public (bool RevealAi, bool InfiniteTokens) Snapshot()
    {
        lock (_sync) return (_revealAi, _infiniteTokens);
    }

    public void Set(bool? revealAi, bool? infiniteTokens)
    {
        lock (_sync)
        {
            if (revealAi.HasValue) _revealAi = revealAi.Value;
            if (infiniteTokens.HasValue) _infiniteTokens = infiniteTokens.Value;
        }
    }

    public bool RevealAi { get { lock (_sync) return _revealAi; } }
    public bool InfiniteTokens { get { lock (_sync) return _infiniteTokens; } }
}
