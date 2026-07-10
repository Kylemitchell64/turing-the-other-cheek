using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using GameApi.Auth;
using GameApi.Data;
using GameApi.Models;
using Xunit;

namespace GameApi.Tests;

// Phase 13: POST /api/profile/username — a fresh OAuth account (NeedsUsername) picks a
// name. Free name -> set it; guest's name -> merge that guest in and claim it; a
// non-guest's name -> 409. Also checks the NeedsUsername flag flows through the auth shape.
public class UsernameClaimTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public UsernameClaimTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task PendingOAuthUser_HasNeedsUsername_ClearedAfterClaim()
    {
        var (client, _) = await PendingOAuthClientAsync();

        var me = await client.GetFromJsonAsync<MeResponse>("/api/auth/me");
        Assert.True(me!.NeedsUsername);
        Assert.False(me.IsGuest);

        var name = FreshName();
        var res = await client.PostAsJsonAsync("/api/profile/username", new { username = name });
        res.EnsureSuccessStatusCode();
        var auth = await res.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.Equal(name, auth!.Username);
        Assert.Equal(name, auth.DisplayName);
        Assert.False(auth.NeedsUsername);

        // The fresh token reflects the cleared flag too.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        var me2 = await client.GetFromJsonAsync<MeResponse>("/api/auth/me");
        Assert.False(me2!.NeedsUsername);
        Assert.Equal(name, me2.Username);
    }

    [Fact]
    public async Task ClaimFreeName_TakesIt()
    {
        var (client, _) = await PendingOAuthClientAsync();
        var name = FreshName();
        var res = await client.PostAsJsonAsync("/api/profile/username", new { username = name });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // The name is now claimed: a guest can no longer take it.
        var guest = await _factory.CreateClient()
            .PostAsJsonAsync("/api/auth/guest", new { username = name });
        Assert.Equal(HttpStatusCode.Conflict, guest.StatusCode);
    }

    [Fact]
    public async Task ClaimNonGuestName_Conflicts()
    {
        // A password account owns the name.
        var taken = FreshName();
        var reg = await _factory.CreateClient().PostAsJsonAsync("/api/auth/register",
            new { username = taken, displayName = taken, password = "Password123" });
        reg.EnsureSuccessStatusCode();

        var (client, _) = await PendingOAuthClientAsync();
        var res = await client.PostAsJsonAsync("/api/profile/username", new { username = taken });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task ClaimGuestName_MergesGuestAndDeletesIt()
    {
        // Create a guest and give it a body of data to be inherited.
        var guestName = FreshName();
        var guestClient = _factory.CreateClient();
        var gres = await guestClient.PostAsJsonAsync("/api/auth/guest", new { username = guestName });
        gres.EnsureSuccessStatusCode();
        var gbody = await gres.Content.ReadFromJsonAsync<AuthResponse>();
        guestClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", gbody!.Token);
        var gme = await guestClient.GetFromJsonAsync<MeResponse>("/api/auth/me");
        var guestId = gme!.Id;

        SeedGuestData(guestId);

        // The claiming OAuth user has NO character of its own, so it should inherit the guest's.
        var (client, oauthId) = await PendingOAuthClientAsync();
        var res = await client.PostAsJsonAsync("/api/profile/username", new { username = guestName });
        res.EnsureSuccessStatusCode();
        var auth = await res.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.Equal(guestName, auth!.Username);
        Assert.False(auth.NeedsUsername);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();

        // Guest row gone; its data now belongs to the OAuth account.
        Assert.False(db.Users.Any(u => u.Id == guestId));
        Assert.True(db.WritingSamples.Any(s => s.UserId == oauthId && s.Text == "carried sample"));
        Assert.False(db.WritingSamples.Any(s => s.UserId == guestId));

        var stats = db.PlayerStats.Single(s => s.UserId == oauthId);
        Assert.Equal(5, stats.GamesPlayed);
        Assert.Equal(2, stats.DetectorWins);

        var merged = db.Users.Single(u => u.Id == oauthId);
        Assert.Equal("{\"base\":1,\"hair\":2,\"outfit\":3,\"accessory\":null}", merged.CharacterJson);

        // The message the guest wrote survives, re-pointed to the claiming account.
        var msg = db.GameMessages.Single(m => m.Text == "guest said this");
        Assert.Equal(oauthId, msg.AuthorUserId);
    }

    // --- infra ---

    private void SeedGuestData(string guestId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();

        var guest = db.Users.Single(u => u.Id == guestId);
        guest.CharacterJson = "{\"base\":1,\"hair\":2,\"outfit\":3,\"accessory\":null}";

        db.WritingSamples.Add(new WritingSample { UserId = guestId, Text = "carried sample", Source = SampleSource.Game, CreatedAt = DateTime.UtcNow });
        db.StyleProfiles.Add(new StyleProfile { UserId = guestId, SummaryJson = "{\"avgLength\":30}", UpdatedAt = DateTime.UtcNow });
        db.PlayerStats.Add(new PlayerStats { UserId = guestId, GamesPlayed = 5, DetectorWins = 2 });

        var game = new Game { JoinCode = "MERGE", State = GameState.Ended, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow };
        game.Players.Add(new GamePlayer { UserId = guestId });
        game.Messages.Add(new GameMessage { Round = 1, AuthorUserId = guestId, AuthorDisplayNameAtTime = "guesty", Text = "guest said this", SentAt = DateTime.UtcNow });
        db.Games.Add(game);

        db.SaveChanges();
    }

    // Seeds a brand-new external account (IsGuest=false, NeedsUsername=true) and returns
    // an authed client for it (token minted the same way the OAuth callback would).
    private async Task<(HttpClient client, string userId)> PendingOAuthClientAsync()
    {
        var id = "oauth_" + Guid.NewGuid().ToString("N");
        var placeholder = ("ext" + Guid.NewGuid().ToString("N"))[..16];

        string token;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();
            db.Users.Add(new ApplicationUser
            {
                Id = id, UserName = placeholder, DisplayName = placeholder,
                IsGuest = false, NeedsUsername = true,
                ExternalProvider = "Google", ExternalId = id, LastSeenUtc = DateTime.UtcNow
            });
            db.SaveChanges();

            var user = db.Users.Single(u => u.Id == id);
            token = scope.ServiceProvider.GetRequiredService<JwtTokenService>().GenerateJwt(user);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, id);
    }

    private static string FreshName() => "u" + Guid.NewGuid().ToString("N")[..12];

    private record AuthResponse(string Token, string DisplayName, string Username, bool IsGuest, bool NeedsUsername);
    private record MeResponse(string Id, string Username, string DisplayName, bool IsGuest, bool NeedsUsername);
}
