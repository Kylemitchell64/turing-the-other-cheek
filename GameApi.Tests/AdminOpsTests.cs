using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using GameApi.Auth;
using GameApi.Data;
using GameApi.Models;
using Xunit;

namespace GameApi.Tests;

// Phase 30 operator tooling: user directory filters/sort/storage, cleanup (dry run vs real),
// the self-check run (every step reports, the synthetic game chain passes on the mock
// brain), and cheats (toggle endpoint; the private AI reveal reaches an admin seat only).
// Uses AdminAppFactory (its own in-memory DB + admin allowlist).
public class AdminOpsTests : IClassFixture<AdminAppFactory>
{
    private readonly AdminAppFactory _factory;

    public AdminOpsTests(AdminAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Users_FilterSortAndStorage_Work()
    {
        var (_, adminToken) = await SeedAdminAsync();
        var client = AuthedClient(adminToken);

        // a heavy, recently-seen guest with a sample; a probe guest (never played, no samples,
        // last seen 2 days ago); an inactive password user (seen 40 days ago)
        var heavy = await SeedUserAsync("heavy", guest: true, lastSeen: DateTime.UtcNow);
        var probe = await SeedUserAsync("probe", guest: true, lastSeen: DateTime.UtcNow.AddDays(-2));
        var dormant = await SeedUserAsync("dormant", guest: false, lastSeen: DateTime.UtcNow.AddDays(-40));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();
            db.WritingSamples.Add(new WritingSample { UserId = heavy.id, Text = new string('x', 5000), Source = SampleSource.Upload, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        using var byStorage = await GetJson(client, "/api/admin/users?sort=storage&pageSize=100");
        var first = byStorage.RootElement.GetProperty("users")[0];
        Assert.Equal(heavy.name, first.GetProperty("displayName").GetString());
        Assert.True(first.GetProperty("dataUsage").GetInt64() > 5000);
        Assert.Equal(1, first.GetProperty("samples").GetInt32());

        using var safe = await GetJson(client, "/api/admin/users?filter=safe-delete&pageSize=100");
        var safeNames = safe.RootElement.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("displayName").GetString()).ToList();
        Assert.Contains(probe.name, safeNames);
        Assert.DoesNotContain(heavy.name, safeNames);   // has a sample
        Assert.DoesNotContain(dormant.name, safeNames); // not a guest
        Assert.All(safe.RootElement.GetProperty("users").EnumerateArray(), u => Assert.True(u.GetProperty("safeDelete").GetBoolean()));

        using var inactive = await GetJson(client, "/api/admin/users?filter=inactive&pageSize=100");
        var inactiveNames = inactive.RootElement.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("displayName").GetString()).ToList();
        Assert.Contains(dormant.name, inactiveNames);
        Assert.DoesNotContain(heavy.name, inactiveNames);

        using var byName = await GetJson(client, "/api/admin/users?sort=name&search=u_&pageSize=100");
        var names = byName.RootElement.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("displayName").GetString()!).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), names);
    }

    [Fact]
    public async Task Cleanup_DryRunCounts_RealRunDeletesOnlySafeAccounts()
    {
        var (_, adminToken) = await SeedAdminAsync();
        var client = AuthedClient(adminToken);
        var probe = await SeedUserAsync("probe2", guest: true, lastSeen: DateTime.UtcNow.AddDays(-3));
        var keeper = await SeedUserAsync("keeper", guest: true, lastSeen: DateTime.UtcNow); // seen just now: kept

        var dry = await client.PostAsync("/api/admin/cleanup", JsonContent.Create(new { dryRun = true }));
        dry.EnsureSuccessStatusCode();
        var dryBody = await dry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dryBody.GetProperty("dryRun").GetBoolean());
        Assert.True(dryBody.GetProperty("safeDelete").GetInt32() >= 1);
        Assert.True(await UserExists(probe.id)); // dry run touched nothing

        var refused = await client.PostAsync("/api/admin/cleanup", JsonContent.Create(new { dryRun = false, confirm = "nope" }));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var real = await client.PostAsync("/api/admin/cleanup", JsonContent.Create(new { dryRun = false, confirm = "CLEANUP" }));
        real.EnsureSuccessStatusCode();
        var body = await real.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("dryRun").GetBoolean());
        Assert.True(body.GetProperty("removedUsers").GetInt32() >= 1);
        Assert.False(await UserExists(probe.id));
        Assert.True(await UserExists(keeper.id));
    }

    [Fact]
    public async Task SelfCheck_RunsEveryStep_AndTheGameChainPasses()
    {
        var (_, adminToken) = await SeedAdminAsync();
        var client = AuthedClient(adminToken);

        var start = await client.PostAsync("/api/admin/selfcheck", JsonContent.Create(new { }));
        start.EnsureSuccessStatusCode();

        JsonElement run = default;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            using var doc = await GetJson(client, "/api/admin/selfcheck");
            run = doc.RootElement.Clone();
            if (!run.GetProperty("running").GetBoolean() && run.GetProperty("steps").GetArrayLength() >= 7) break;
            await Task.Delay(300);
        }
        Assert.False(run.GetProperty("running").GetBoolean(), "self-check never finished");

        var steps = run.GetProperty("steps").EnumerateArray().ToDictionary(s => s.GetProperty("key").GetString()!, s => s);
        foreach (var key in new[] { "db", "migrations", "config", "ai", "game", "lobbies", "freetier" })
            Assert.True(steps.ContainsKey(key), $"missing step {key}");

        // in-memory test store: db passes (warn is for the prod memory fallback, which this is not)
        Assert.Contains(steps["db"].GetProperty("status").GetString(), new[] { "pass", "warn" });
        Assert.Equal("pass", steps["migrations"].GetProperty("status").GetString());
        // no AI key in tests → the ping is skipped with a warn, never a fail
        Assert.Equal("warn", steps["ai"].GetProperty("status").GetString());
        // the synthetic game ran to the end on the mock brain with the scripted fake-out
        var game = steps["game"];
        Assert.Equal("pass", game.GetProperty("status").GetString());
        Assert.Contains("2 rounds", game.GetProperty("detail").GetString());
        Assert.Contains("faked-out", game.GetProperty("detail").GetString());
        Assert.Equal("pass", steps["freetier"].GetProperty("status").GetString());

        // the synthetic lobby was cleaned up
        var store = _factory.Services.GetRequiredService<GameApi.Lobbies.LobbyStore>();
        Assert.DoesNotContain(store.All, l => l.IsSelfCheck);
    }

    [Fact]
    public async Task Cheats_RevealAi_ReachesOnlyAdminSeats()
    {
        var (_, adminToken) = await SeedAdminAsync();
        var admin = AuthedClient(adminToken);

        var set = await admin.PostAsync("/api/admin/cheats", JsonContent.Create(new { revealAi = true }));
        set.EnsureSuccessStatusCode();
        var state = await set.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(state.GetProperty("revealAi").GetBoolean());
        Assert.False(state.GetProperty("infiniteTokens").GetBoolean());

        try
        {
            // admin hosts a solo game: they get CheatReveal with the AI's roster name
            await using var conn = BuildConnection(adminToken);
            string? revealed = null;
            List<JsonElement>? roster = null;
            conn.On<string>("CheatReveal", n => revealed = n);
            conn.On<List<JsonElement>>("GameStarted", r => roster = r);
            await conn.StartAsync();
            await conn.InvokeAsync("CreateLobby");
            await conn.InvokeAsync("StartSoloGame");
            await WaitFor(() => revealed != null && roster != null, "admin never got CheatReveal");
            Assert.Contains(roster!, e => e.GetProperty("displayName").GetString() == revealed);

            // a plain user hosting their own solo game gets nothing
            var plain = await SeedUserAsync("plain", guest: false, lastSeen: DateTime.UtcNow);
            await using var conn2 = BuildConnection(plain.token);
            string? leaked = null;
            List<JsonElement>? roster2 = null;
            conn2.On<string>("CheatReveal", n => leaked = n);
            conn2.On<List<JsonElement>>("GameStarted", r => roster2 = r);
            await conn2.StartAsync();
            await conn2.InvokeAsync("CreateLobby");
            await conn2.InvokeAsync("StartSoloGame");
            await WaitFor(() => roster2 != null, "plain user never got GameStarted");
            await Task.Delay(300);
            Assert.Null(leaked);
        }
        finally
        {
            await admin.PostAsync("/api/admin/cheats", JsonContent.Create(new { revealAi = false, infiniteTokens = false }));
        }
    }

    // --- helpers ---

    private async Task<(string id, string token)> SeedAdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var tokens = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
        var name = "admin_" + Guid.NewGuid().ToString("N")[..8];
        var user = new ApplicationUser
        {
            UserName = name, DisplayName = name, Email = AdminAppFactory.AdminEmail,
            ExternalProvider = "Google", ExternalId = Guid.NewGuid().ToString("N"), LastSeenUtc = DateTime.UtcNow
        };
        var res = await users.CreateAsync(user);
        Assert.True(res.Succeeded, "seed admin failed");
        return (user.Id, tokens.GenerateJwt(user));
    }

    private async Task<(string id, string token, string name)> SeedUserAsync(string tag, bool guest, DateTime? lastSeen)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var tokens = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
        var name = $"u_{tag}_" + Guid.NewGuid().ToString("N")[..6];
        var user = new ApplicationUser { UserName = name, DisplayName = name, IsGuest = guest, LastSeenUtc = lastSeen };
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
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(message);
    }
}
