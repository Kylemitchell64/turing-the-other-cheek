using System.Net.Http.Headers;
using System.Text.Json;

namespace GameApi.Auth;

// Feature-flagged, server-side OAuth (authorization-code flow) for Google + GitHub.
// Plain HTTP via IHttpClientFactory — no provider SDKs, no external-cookie middleware.
// A provider is "configured" only when both its client id and secret are present, so
// the flag falls out of config with nothing extra to wire.
public class OAuthService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<OAuthService> _logger;

    public OAuthService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<OAuthService> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public static readonly string[] KnownProviders = { "google", "github" };

    public static bool IsKnownProvider(string provider) =>
        KnownProviders.Contains(provider.ToLowerInvariant());

    // Env var wins, then appsettings/user-secrets. Mirrors the JWT/Gemini key lookup.
    private string? Get(string key) =>
        Environment.GetEnvironmentVariable(key) ?? _config[key];

    public bool IsConfigured(string provider)
    {
        var (idKey, secretKey) = provider.ToLowerInvariant() switch
        {
            "google" => ("GOOGLE_CLIENT_ID", "GOOGLE_CLIENT_SECRET"),
            "github" => ("GITHUB_CLIENT_ID", "GITHUB_CLIENT_SECRET"),
            _ => (null, null)
        };
        if (idKey == null) return false;
        return !string.IsNullOrWhiteSpace(Get(idKey)) && !string.IsNullOrWhiteSpace(Get(secretKey!));
    }

    // Where the SPA lives (for the final redirect back with the token fragment). Env
    // CLIENT_URL wins; otherwise the Vercel URL in prod, Vite dev server in dev.
    public string ClientUrl(bool isDevelopment)
    {
        var configured = Get("CLIENT_URL");
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');
        return isDevelopment
            ? "http://localhost:5173"
            : "https://turing-the-other-cheek.vercel.app";
    }

    public string BuildAuthorizeUrl(string provider, string redirectUri, string state)
    {
        provider = provider.ToLowerInvariant();
        var clientId = Get(provider == "google" ? "GOOGLE_CLIENT_ID" : "GITHUB_CLIENT_ID")!;
        return provider switch
        {
            "google" =>
                "https://accounts.google.com/o/oauth2/v2/auth" +
                $"?client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&response_type=code" +
                "&scope=" + Uri.EscapeDataString("openid email profile") +
                $"&state={Uri.EscapeDataString(state)}",
            "github" =>
                "https://github.com/login/oauth/authorize" +
                $"?client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&scope=" + Uri.EscapeDataString("read:user user:email") +
                $"&state={Uri.EscapeDataString(state)}",
            _ => throw new ArgumentException($"unknown provider {provider}")
        };
    }

    // Exchanges the auth code and returns the provider's stable id + a display-name hint.
    // Returns null on any failure (bad code, network, missing fields) — the caller turns
    // that into a user-facing error redirect.
    public async Task<ExternalIdentity?> ExchangeAsync(string provider, string code, string redirectUri, CancellationToken ct)
    {
        provider = provider.ToLowerInvariant();
        try
        {
            return provider switch
            {
                "google" => await ExchangeGoogleAsync(code, redirectUri, ct),
                "github" => await ExchangeGitHubAsync(code, redirectUri, ct),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OAuth exchange failed for {Provider}", provider);
            return null;
        }
    }

    private async Task<ExternalIdentity?> ExchangeGoogleAsync(string code, string redirectUri, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("oauth");
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = Get("GOOGLE_CLIENT_ID")!,
                ["client_secret"] = Get("GOOGLE_CLIENT_SECRET")!,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code"
            })
        };
        var tokenRes = await http.SendAsync(tokenReq, ct);
        if (!tokenRes.IsSuccessStatusCode) return null;

        using var tokenDoc = JsonDocument.Parse(await tokenRes.Content.ReadAsStringAsync(ct));
        if (!tokenDoc.RootElement.TryGetProperty("id_token", out var idTokenEl)) return null;

        // The id_token is a signed JWT; we received it directly from Google over TLS, so
        // reading its payload for sub/email is sufficient here (no third-party relay).
        var payload = DecodeJwtPayload(idTokenEl.GetString());
        if (payload is null) return null;

        var sub = payload.Value.TryGetProperty("sub", out var s) ? s.GetString() : null;
        if (string.IsNullOrEmpty(sub)) return null;
        var email = payload.Value.TryGetProperty("email", out var e) ? e.GetString() : null;
        var name = payload.Value.TryGetProperty("name", out var n) ? n.GetString() : null;

        return new ExternalIdentity("Google", sub, email, name ?? EmailLocalPart(email));
    }

    private async Task<ExternalIdentity?> ExchangeGitHubAsync(string code, string redirectUri, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("oauth");
        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = Get("GITHUB_CLIENT_ID")!,
                ["client_secret"] = Get("GITHUB_CLIENT_SECRET")!,
                ["redirect_uri"] = redirectUri
            })
        };
        tokenReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var tokenRes = await http.SendAsync(tokenReq, ct);
        if (!tokenRes.IsSuccessStatusCode) return null;

        using var tokenDoc = JsonDocument.Parse(await tokenRes.Content.ReadAsStringAsync(ct));
        if (!tokenDoc.RootElement.TryGetProperty("access_token", out var accessEl)) return null;
        var accessToken = accessEl.GetString();
        if (string.IsNullOrEmpty(accessToken)) return null;

        var userReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        userReq.Headers.UserAgent.ParseAdd("turing-the-other-cheek");
        userReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var userRes = await http.SendAsync(userReq, ct);
        if (!userRes.IsSuccessStatusCode) return null;

        using var userDoc = JsonDocument.Parse(await userRes.Content.ReadAsStringAsync(ct));
        var root = userDoc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : null;
        if (string.IsNullOrEmpty(id)) return null;
        var login = root.TryGetProperty("login", out var loginEl) ? loginEl.GetString() : null;
        var name = root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString() : null;
        var email = root.TryGetProperty("email", out var emailEl) && emailEl.ValueKind == JsonValueKind.String
            ? emailEl.GetString() : null;

        return new ExternalIdentity("GitHub", id, email, name ?? login);
    }

    private static JsonElement? DecodeJwtPayload(string? jwt)
    {
        if (string.IsNullOrEmpty(jwt)) return null;
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? EmailLocalPart(string? email)
    {
        if (string.IsNullOrEmpty(email)) return null;
        var at = email.IndexOf('@');
        return at > 0 ? email[..at] : email;
    }
}

// What we pull out of a provider: their stable id, plus best-effort email + display name.
public record ExternalIdentity(string Provider, string ExternalId, string? Email, string? DisplayNameHint);
