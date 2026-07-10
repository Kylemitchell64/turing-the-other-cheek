namespace GameApi.Admin;

// Process-wide maintenance switch (phase 18). When on, the hub refuses to create/join
// lobbies or start games (running games finish naturally), and the public /api/status
// endpoint reports it so the client can banner it. In-memory singleton — a self-restart
// clears it, which is fine: maintenance is an operator's live "pause new games" lever.
public class MaintenanceState
{
    private readonly object _lock = new();
    private bool _on;
    private string? _message;

    public (bool On, string? Message) Snapshot()
    {
        lock (_lock) return (_on, _message);
    }

    public bool IsOn
    {
        get { lock (_lock) return _on; }
    }

    public string? Message
    {
        get { lock (_lock) return _message; }
    }

    public void Set(bool on, string? message)
    {
        lock (_lock)
        {
            _on = on;
            _message = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        }
    }
}
