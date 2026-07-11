using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using GameApi.Auth;
using GameApi.Data;
using GameApi.Models;
using Xunit;

namespace GameApi.Tests;

// Phase 18: the admin dashboard API. Boots the app with an ADMIN_EMAILS allowlist and its
// OWN in-memory DB (so the db-wipe test can't disturb the rest of the suite). "Admin" tokens
// are minted straight from JwtTokenService for a seeded Google account whose email is on the
// allowlist — exactly the claim shape the "AdminOnly" policy checks.
public class AdminAppFactory : TestAppFactory
{
    public const string AdminEmail = "operator@turing.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ADMIN_EMAILS"] = AdminEmail
            });
        });

        // Isolate this factory's data from the shared "turing-tests" store so wipe is safe.
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<GameContext>>();
            services.RemoveAll<GameContext>();
            services.AddDbContext<GameContext>(o => o.UseInMemoryDatabase("turing-admin-tests"));
        });
    }
}

public class AdminApiTests : IClassFixture<AdminAppFactory>
{
    private readonly AdminAppFactory _factory;

    public AdminApiTests(AdminAppFactory factory) => _factory = factory;

    // Every /api/admin route rejects an authenticated non-admin with 403 (and restart, being
    // gated first, never actually fires for them).
    [Fact]
    public async Task NonAdmin_Gets403_OnEveryRoute()
    {
        var (uid, token) = await SeedUserAsync();
        var client = AuthedClient(token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/timeline")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/freetier")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/api/admin/maintenance", Json(new { on = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/api/admin/restart", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/api/admin/wipe", Json(new { confirm = "WIPE EVERYTHING" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync($"/api/admin/users/{uid}/rewards", Json(new { kind = "outfit:17" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.DeleteAsync($"/api/admin/users/{uid}/rewards?kind=outfit:17")).StatusCode);

        // Anonymous is a 401, not a 403.
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/overview")).StatusCode);
    }

    // The four read endpoints return 200 with the shapes the dashboard consumes.
    [Fact]
    public async Task Admin_ReadEndpoints_HaveSaneShapes()
    {
        var (_, adminToken) = await SeedAdminAsync();
        await SeedUserAsync();
        await SeedUserAsync(guest: true);
        var client = AuthedClient(adminToken);

        using var overview = await GetJson(client, "/api/admin/overview");
        var o = overview.RootElement;
        Assert.True(o.GetProperty("totalUsers").GetInt32() >= 2);
        Assert.True(o.TryGetProperty("guests", out _));
        Assert.True(o.TryGetProperty("activeLobbies", out _));
        Assert.True(o.TryGetProperty("gamesTotal", out _));
        Assert.Equal(JsonValueKind.Array, o.GetProperty("aiProviders").ValueKind);

        using var timeline = await GetJson(client, "/api/admin/timeline");
        var days = timeline.RootElement.GetProperty("days");
        Assert.Equal(JsonValueKind.Array, days.ValueKind);
        Assert.Equal(30, days.GetArrayLength());
        foreach (var d in days.EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(d.GetProperty("date").GetString()));
            Assert.True(d.GetProperty("count").GetInt32() >= 0);
        }

        using var freetier = await GetJson(client, "/api/admin/freetier");
        var resources = freetier.RootElement.GetProperty("resources");
        Assert.Equal(JsonValueKind.Array, resources.ValueKind);
        Assert.True(resources.GetArrayLength() >= 2); // supabase + render at minimum
        var avg = freetier.RootElement.GetProperty("average").GetDouble();
        Assert.InRange(avg, 0, 100);

        using var users = await GetJson(client, "/api/admin/users");
        var u = users.RootElement;
        Assert.True(u.GetProperty("total").GetInt32() >= 2);
        Assert.True(u.TryGetProperty("maxDataUsage", out _));
        var list = u.GetProperty("users");
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        var first = list.EnumerateArray().First();
        Assert.False(string.IsNullOrEmpty(first.GetProperty("id").GetString()));
        Assert.False(string.IsNullOrEmpty(first.GetProperty("tier").GetString()));
        Assert.True(first.GetProperty("rewards").TryGetProperty("cheatCards", out _));
    }

    // A granted premium outfit unlocks that id for the recipient's PUT /profile/character —
    // and only theirs; an ungranted user still gets 400 saving the same locked look.
    [Fact]
    public async Task RewardGrant_UnlocksPremiumOutfit_ForRecipientOnly()
    {
        var (_, adminToken) = await SeedAdminAsync();
        var (targetId, targetToken) = await SeedUserAsync();
        var (_, otherToken) = await SeedUserAsync();
        var admin = AuthedClient(adminToken);

        const string premium = "{\"base\":1,\"hair\":1,\"outfit\":17,\"accessory\":null}";

        // Before the grant, even the target can't save outfit 17.
        Assert.Equal(HttpStatusCode.BadRequest, (await PutCharacter(targetToken, premium)).StatusCode);

        var grant = await admin.PostAsync($"/api/admin/users/{targetId}/rewards", Json(new { kind = "outfit:17" }));
        grant.EnsureSuccessStatusCode();
        using var granted = JsonDocument.Parse(await grant.Content.ReadAsStringAsync());
        Assert.Contains(17, granted.RootElement.GetProperty("outfits").EnumerateArray().Select(e => e.GetInt32()));

        // Now the recipient can save it; a different user still can't.
        Assert.Equal(HttpStatusCode.OK, (await PutCharacter(targetToken, premium)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutCharacter(otherToken, premium)).StatusCode);
    }

    // A granted cheat card seats its holder with a 4th fake-out token at game start and is
    // stamped consumed once the start succeeds. Mirrors the 3-client hub start pattern.
    [Fact]
    public async Task CheatCardGrant_SeatsHolderWith4Tokens_AndConsumes()
    {
        var (_, adminToken) = await SeedAdminAsync();
        var (hostId, hostToken, hostName) = await SeedNamedUserAsync();
        var (_, t2, _) = await SeedNamedUserAsync();
        var (_, t3, _) = await SeedNamedUserAsync();
        var admin = AuthedClient(adminToken);

        // Grant the cheat card through the admin endpoint, and confirm the holder now shows it.
        (await admin.PostAsync($"/api/admin/users/{hostId}/rewards", Json(new { kind = "cheat_card" })))
            .EnsureSuccessStatusCode();
        using var rewards = await GetJson(AuthedClient(hostToken), "/api/profile/rewards");
        Assert.Equal(1, rewards.RootElement.GetProperty("cheatCards").GetInt32());

        var conns = new List<HubConnection>();
        try
        {
            var host = BuildConnection(hostToken);
            var c2 = BuildConnection(t2);
            var c3 = BuildConnection(t3);
            conns.AddRange(new[] { host, c2, c3 });

            LobbyState? lobby = null;
            List<RosterEntry>? roster = null;
            host.On<LobbyState>("LobbyUpdated", s => lobby = s);
            host.On<List<RosterEntry>>("GameStarted", r => roster = r);

            foreach (var c in conns) await c.StartAsync();
            await host.InvokeAsync("CreateLobby");
            await WaitFor(() => lobby != null, "no LobbyUpdated");
            var code = lobby!.Code;
            await c2.InvokeAsync("JoinLobby", code);
            await c3.InvokeAsync("JoinLobby", code);
            await WaitFor(() => lobby!.Players.Count == 3, "not all 3 joined");

            await host.InvokeAsync("StartGame");
            await WaitFor(() => roster != null, "no GameStarted");

            // The cheat-card holder seats with 4 tokens; a plain player still has 3.
            var hostSeat = roster!.First(e => e.DisplayName == hostName);
            Assert.Equal(4, hostSeat.TokensRemaining);
            Assert.Contains(roster!, e => e.TokensRemaining == 3);

            // The reward was stamped consumed by the successful start.
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();
            var card = await db.UserRewards.SingleAsync(r => r.UserId == hostId && r.Kind == "cheat_card");
            Assert.NotNull(card.ConsumedAt);
        }
        finally
        {
            foreach (var c in conns) await c.DisposeAsync();
        }
    }

    // Maintenance on: the hub refuses new lobbies with the operator's message. Off again:
    // CreateLobby works.
    [Fact]
    public async Task Maintenance_On_BlocksCreateLobby_Off_Allows()
    {
        var (_, adminToken) = await SeedAdminAsync();
        var (_, userToken, _) = await SeedNamedUserAsync();
        var admin = AuthedClient(adminToken);

        try
        {
            (await admin.PostAsync("/api/admin/maintenance", Json(new { on = true, message = "brb, upgrading" })))
                .EnsureSuccessStatusCode();

            await using (var conn = BuildConnection(userToken))
            {
                await conn.StartAsync();
                var ex = await Assert.ThrowsAsync<HubException>(() => conn.InvokeAsync("CreateLobby"));
                Assert.Contains("brb, upgrading", ex.Message);
            }

            (await admin.PostAsync("/api/admin/maintenance", Json(new { on = false })))
                .EnsureSuccessStatusCode();

            await using (var conn = BuildConnection(userToken))
            {
                LobbyState? lobby = null;
                conn.On<LobbyState>("LobbyUpdated", s => lobby = s);
                await conn.StartAsync();
                await conn.InvokeAsync("CreateLobby");
                await WaitFor(() => lobby != null, "create lobby didn't work after maintenance off");
            }
        }
        finally
        {
            // Never leave the shared maintenance flag on for a later test.
            await admin.PostAsync("/api/admin/maintenance", Json(new { on = false }));
        }
    }

    // Wipe needs the exact confirm phrase; the wrong phrase changes nothing. The real wipe
    // deletes a seeded guest but leaves the admin account able to sign back in.
    [Fact]
    public async Task Wipe_RequiresExactPhrase_SparesAdmin_DeletesGuest()
    {
        var (adminId, adminToken) = await SeedAdminAsync();
        var (guestId, _) = await SeedUserAsync(guest: true);
        var admin = AuthedClient(adminToken);

        var wrong = await admin.PostAsync("/api/admin/wipe", Json(new { confirm = "wipe everything" }));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.True(await UserExists(guestId), "wrong phrase must not delete anything");

        var right = await admin.PostAsync("/api/admin/wipe", Json(new { confirm = "WIPE EVERYTHING" }));
        right.EnsureSuccessStatusCode();

        Assert.False(await UserExists(guestId), "guest should be wiped");
        Assert.True(await UserExists(adminId), "admin must be spared");
    }

    // Every mutating per-user route also 403s a non-admin (the new profile/delete endpoints).
    [Fact]
    public async Task NonAdmin_Gets403_OnUserProfileAndDelete()
    {
        var (uid, token) = await SeedUserAsync();
        var client = AuthedClient(token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/admin/users/{uid}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/admin/users/{uid}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/api/admin/users/purge-nonoauth", Json(new { confirm = "DELETE GUESTS" }))).StatusCode);
    }

    // GET /users/{id} returns the synopsis shape the profile drawer consumes: identity, the
    // PlayerStats counters, sample count + total chars (never the raw text), and rewards.
    [Fact]
    public async Task UserProfile_ReturnsSynopsisShape()
    {
        var (_, adminToken) = await SeedAdminAsync();
        var (uid, _) = await SeedUserAsync();
        await SeedSampleAsync(uid, "this is how i actually type, promise");
        var admin = AuthedClient(adminToken);

        (await admin.PostAsync($"/api/admin/users/{uid}/rewards", Json(new { kind = "outfit:17" })))
            .EnsureSuccessStatusCode();

        using var doc = await GetJson(admin, $"/api/admin/users/{uid}");
        var p = doc.RootElement;
        Assert.Equal(uid, p.GetProperty("id").GetString());
        Assert.False(string.IsNullOrEmpty(p.GetProperty("displayName").GetString()));
        Assert.False(string.IsNullOrEmpty(p.GetProperty("provider").GetString()));
        Assert.Equal(1, p.GetProperty("sampleCount").GetInt32());
        Assert.True(p.GetProperty("sampleChars").GetInt32() > 0);
        Assert.True(p.TryGetProperty("gamesPlayed", out _));
        Assert.True(p.TryGetProperty("timesReadByAi", out _));
        Assert.False(p.GetProperty("hasCharacter").GetBoolean());
        Assert.Equal(JsonValueKind.Array, p.GetProperty("crews").ValueKind);
        Assert.Contains(17, p.GetProperty("rewards").GetProperty("outfits").EnumerateArray().Select(e => e.GetInt32()));
        // The raw sample text must NOT be exposed anywhere in the synopsis.
        Assert.DoesNotContain("promise", doc.RootElement.GetRawText());

        // Missing user → 404.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/admin/users/does-not-exist")).StatusCode);
    }

    // DELETE /users/{id} removes the account AND its data, even past the RESTRICT foreign keys
    // (a game-participation row and an owned crew) that would otherwise block the delete.
    [Fact]
    public async Task DeleteUser_CascadesData_AcrossRestrictFks()
    {
        var (_, adminToken) = await SeedAdminAsync();
        var (uid, _) = await SeedUserAsync();
        await SeedSampleAsync(uid, "sample text");
        await SeedStatsAndRewardAsync(uid);
        await SeedGameParticipationAsync(uid); // RESTRICT #1
        await SeedOwnedCrewAsync(uid);         // RESTRICT #2
        var admin = AuthedClient(adminToken);

        var del = await admin.DeleteAsync($"/api/admin/users/{uid}");
        del.EnsureSuccessStatusCode();

        Assert.False(await UserExists(uid), "account should be gone");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        Assert.False(await db.WritingSamples.AnyAsync(s => s.UserId == uid), "samples cascade");
        Assert.False(await db.PlayerStats.AnyAsync(s => s.UserId == uid), "stats cascade");
        Assert.False(await db.UserRewards.AnyAsync(r => r.UserId == uid), "rewards cascade");
        Assert.False(await db.CrewMembers.AnyAsync(m => m.UserId == uid), "crew memberships cascade");
        Assert.False(await db.GamePlayers.AnyAsync(g => g.UserId == uid), "participation rows cleared");
    }

    // An admin account (allowlisted email) is never deletable, even by another admin.
    [Fact]
    public async Task DeleteUser_Admin_IsProtected()
    {
        var (adminId, adminToken) = await SeedAdminAsync();
        var admin = AuthedClient(adminToken);

        var del = await admin.DeleteAsync($"/api/admin/users/{adminId}");
        Assert.Equal(HttpStatusCode.BadRequest, del.StatusCode);
        Assert.True(await UserExists(adminId), "admin must survive a delete attempt");
    }

    // Bulk purge deletes every non-oauth account (guests + password) and spares oauth logins,
    // returning the count. The wrong confirm phrase changes nothing.
    [Fact]
    public async Task PurgeNonOauth_DeletesGuestsAndPassword_SparesOAuth()
    {
        var (adminId, adminToken) = await SeedAdminAsync();
        var (guest1, _) = await SeedUserAsync(guest: true);
        var (guest2, _) = await SeedUserAsync(guest: true);
        var (pwUser, _) = await SeedUserAsync();      // password account = non-oauth too
        var admin = AuthedClient(adminToken);

        var wrong = await admin.PostAsync("/api/admin/users/purge-nonoauth", Json(new { confirm = "nope" }));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.True(await UserExists(guest1), "wrong phrase must not delete anything");

        var res = await admin.PostAsync("/api/admin/users/purge-nonoauth", Json(new { confirm = "DELETE GUESTS" }));
        res.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("deleted").GetInt32() >= 3);

        Assert.False(await UserExists(guest1), "guest purged");
        Assert.False(await UserExists(guest2), "guest purged");
        Assert.False(await UserExists(pwUser), "password account purged");
        Assert.True(await UserExists(adminId), "oauth admin spared");

        // Deterministic: no non-oauth (empty-provider) accounts remain at all.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        Assert.False(await db.Users.AnyAsync(u => u.ExternalProvider == null || u.ExternalProvider == ""),
            "no non-oauth accounts should survive the purge");
    }

    // --- seeding + infra ---

    private async Task SeedSampleAsync(string userId, string text)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        db.WritingSamples.Add(new WritingSample
        {
            UserId = userId, Text = text, Source = SampleSource.Upload, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedStatsAndRewardAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        db.PlayerStats.Add(new PlayerStats { UserId = userId, GamesPlayed = 3, TimesReadByAi = 1 });
        db.UserRewards.Add(new UserReward { UserId = userId, Kind = "outfit:17", GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task SeedGameParticipationAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        var game = new Game { JoinCode = Guid.NewGuid().ToString("N")[..5], StartedAt = DateTime.UtcNow };
        db.Games.Add(game);
        await db.SaveChangesAsync();
        db.GamePlayers.Add(new GamePlayer { GameId = game.Id, UserId = userId });
        await db.SaveChangesAsync();
    }

    private async Task SeedOwnedCrewAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        var crew = new Crew
        {
            Name = "crew_" + Guid.NewGuid().ToString("N")[..6],
            OwnerUserId = userId,
            JoinCode = Guid.NewGuid().ToString("N")[..5],
            CreatedAt = DateTime.UtcNow
        };
        db.Crews.Add(crew);
        await db.SaveChangesAsync();
        db.CrewMembers.Add(new CrewMember { CrewId = crew.Id, UserId = userId, JoinedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<(string id, string token)> SeedAdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var tokens = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
        var name = "admin_" + Guid.NewGuid().ToString("N")[..8];
        var user = new ApplicationUser
        {
            UserName = name,
            DisplayName = name,
            Email = AdminAppFactory.AdminEmail,
            ExternalProvider = "Google",
            ExternalId = Guid.NewGuid().ToString("N"),
            LastSeenUtc = DateTime.UtcNow
        };
        var res = await users.CreateAsync(user);
        Assert.True(res.Succeeded, "seed admin failed");
        return (user.Id, tokens.GenerateJwt(user));
    }

    private async Task<(string id, string token)> SeedUserAsync(bool guest = false)
    {
        var (id, token, _) = await SeedNamedUserAsync(guest);
        return (id, token);
    }

    private async Task<(string id, string token, string name)> SeedNamedUserAsync(bool guest = false)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var tokens = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
        var name = "u_" + Guid.NewGuid().ToString("N")[..10];
        var user = new ApplicationUser { UserName = name, DisplayName = name, IsGuest = guest, LastSeenUtc = DateTime.UtcNow };
        var res = guest ? await users.CreateAsync(user) : await users.CreateAsync(user, "Password123");
        Assert.True(res.Succeeded, "seed user failed");
        return (user.Id, tokens.GenerateJwt(user), name);
    }

    private async Task<bool> UserExists(string id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        return await db.Users.AnyAsync(u => u.Id == id);
    }

    private HttpClient AuthedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private Task<HttpResponseMessage> PutCharacter(string token, string json)
    {
        var client = AuthedClient(token);
        return client.PutAsync("/api/profile/character", new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static HttpContent Json(object body) => JsonContent.Create(body);

    private static async Task<JsonDocument> GetJson(HttpClient client, string path)
    {
        var res = await client.GetAsync(path);
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
    }

    private HubConnection BuildConnection(string token) =>
        new HubConnectionBuilder()
            .WithUrl(_factory.Server.BaseAddress + "hubs/game", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    private static async Task WaitFor(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(message);
    }

    private record LobbyState(string Code, string State, List<PlayerEntry> Players);
    private record PlayerEntry(string DisplayName, int TokensRemaining, bool IsConnected, bool IsHost);
    private record RosterEntry(string DisplayName, int TokensRemaining);
}
