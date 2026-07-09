using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GameApi.Tests;

// Phase 9: guest login (create / resume / claimed-name rejection) + the providers flag.
public class AuthApiTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public AuthApiTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Guest_CreatesAccount_ReturnsToken()
    {
        var client = _factory.CreateClient();
        var name = UniqueName();

        var res = await client.PostAsJsonAsync("/api/auth/guest", new { username = name });
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.False(string.IsNullOrEmpty(body!.Token));
        Assert.Equal(name, body.Username);
        Assert.Equal(name, body.DisplayName);
    }

    [Fact]
    public async Task Guest_SameName_ResumesSameAccount()
    {
        var client = _factory.CreateClient();
        var name = UniqueName();

        var first = await GuestId(client, name);
        var second = await GuestId(client, name);

        Assert.Equal(first, second); // same account resumed, not a new one
    }

    [Fact]
    public async Task Guest_SameName_IsCaseInsensitive()
    {
        var client = _factory.CreateClient();
        var name = UniqueName();

        var lower = await GuestId(client, name.ToLowerInvariant());
        var upper = await GuestId(client, name.ToUpperInvariant());

        Assert.Equal(lower, upper); // "alpha" and "ALPHA" are the same account
    }

    [Fact]
    public async Task Guest_ClaimedByRealAccount_IsRejected()
    {
        var client = _factory.CreateClient();
        var name = UniqueName();

        // A real (password) account owns the name first.
        var reg = await client.PostAsJsonAsync("/api/auth/register",
            new { username = name, displayName = name, password = "Password123" });
        reg.EnsureSuccessStatusCode();

        var guest = await client.PostAsJsonAsync("/api/auth/guest", new { username = name });
        Assert.Equal(HttpStatusCode.Conflict, guest.StatusCode);
        var err = await guest.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("claimed", err!.Error);
    }

    [Fact]
    public async Task Guest_RealAccountCannotSteal_GuestNameBlocksRegister()
    {
        var client = _factory.CreateClient();
        var name = UniqueName();

        var guest = await client.PostAsJsonAsync("/api/auth/guest", new { username = name });
        guest.EnsureSuccessStatusCode();

        // Same name can't then be registered as a password account (shared namespace).
        var reg = await client.PostAsJsonAsync("/api/auth/register",
            new { username = name, displayName = name, password = "Password123" });
        Assert.Equal(HttpStatusCode.Conflict, reg.StatusCode);
    }

    [Theory]
    [InlineData("ab")]              // too short
    [InlineData("way_too_long_username_here")] // > 20
    [InlineData("has space")]
    [InlineData("bad!name")]
    public async Task Guest_InvalidUsername_IsRejected(string bad)
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/guest", new { username = bad });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Providers_NoneConfigured_BothFalse()
    {
        var client = _factory.CreateClient();
        var res = await client.GetFromJsonAsync<ProvidersResponse>("/api/auth/providers");
        Assert.False(res!.Google);
        Assert.False(res.GitHub);
    }

    [Fact]
    public async Task Providers_Configured_ReflectsConfig()
    {
        using var configured = WithOAuth();
        var client = configured.CreateClient();
        var res = await client.GetFromJsonAsync<ProvidersResponse>("/api/auth/providers");
        Assert.True(res!.Google);
        Assert.True(res.GitHub);
    }

    [Fact]
    public async Task OAuthLogin_NotConfigured_Is404()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await client.GetAsync("/api/auth/google/login");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task OAuthLogin_Configured_RedirectsToProviderWithState()
    {
        using var configured = WithOAuth();
        var client = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await client.GetAsync("/api/auth/google/login");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith("https://accounts.google.com/", res.Headers.Location!.ToString());
        Assert.Contains("state=", res.Headers.Location.ToString());
        // CSRF state is stashed in a cookie for the callback to verify.
        Assert.Contains(res.Headers.GetValues("Set-Cookie"), c => c.StartsWith("oauth_state="));
    }

    // --- infra ---

    private WebApplicationFactory<Program> WithOAuth() =>
        _factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GOOGLE_CLIENT_ID"] = "test-google-id",
                    ["GOOGLE_CLIENT_SECRET"] = "test-google-secret",
                    ["GITHUB_CLIENT_ID"] = "test-github-id",
                    ["GITHUB_CLIENT_SECRET"] = "test-github-secret",
                })));

    private static async Task<string> GuestId(HttpClient client, string name)
    {
        var res = await client.PostAsJsonAsync("/api/auth/guest", new { username = name });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<AuthResponse>();

        var me = client.DefaultRequestHeaders.Authorization;
        var authed = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        authed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        var meRes = await client.SendAsync(authed);
        meRes.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = me;
        var meBody = await meRes.Content.ReadFromJsonAsync<MeResponse>();
        return meBody!.Id;
    }

    private static string UniqueName() => "g" + Guid.NewGuid().ToString("N")[..12];

    private record AuthResponse(string Token, string DisplayName, string Username);
    private record ErrorResponse(string Error);
    private record ProvidersResponse(bool Google, bool GitHub);
    private record MeResponse(string Id, string Username, string DisplayName);
}
