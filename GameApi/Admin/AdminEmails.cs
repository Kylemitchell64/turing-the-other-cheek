namespace GameApi.Admin;

// The allowlist that gates the admin dashboard. ADMIN_EMAILS is a comma-separated list of
// emails (env var on Render, or config). An account is admin when it signed in with Google
// (OAuth accounts carry a verified email) AND that email is on this list — enforced as the
// "AdminOnly" authorization policy over two JWT claims minted at login time.
public static class AdminEmails
{
    // Parse the configured allowlist into a case-insensitive set. Empty when unset — so with
    // no ADMIN_EMAILS configured, nobody is admin and /api/admin/* is locked to everyone.
    public static HashSet<string> Load(IConfiguration config)
    {
        var raw = Environment.GetEnvironmentVariable("ADMIN_EMAILS")
            ?? config["ADMIN_EMAILS"]
            ?? config["Admin:Emails"]
            ?? "";

        return raw
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Is this account an admin? Requires a Google-provider account whose email is allowlisted.
    public static bool IsAdmin(IConfiguration config, string? email, string? externalProvider)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        if (!string.Equals(externalProvider, "Google", StringComparison.OrdinalIgnoreCase)) return false;
        return Load(config).Contains(email.Trim().ToLowerInvariant());
    }
}
