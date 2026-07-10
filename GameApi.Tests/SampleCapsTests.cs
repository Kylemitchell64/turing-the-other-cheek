using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using GameApi.Data;
using GameApi.Models;
using Xunit;

namespace GameApi.Tests;

// Phase 13: the per-tier rolling sample cap (guest 10 / user 200). SampleCaps.EnforceAsync
// is the single code path BOTH the manual-paste endpoint and post-game harvesting call, so
// the helper tests below cover the trim logic for both entry points; the API tests then
// prove the SamplesController wiring + tier resolution.
public class SampleCapsTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public SampleCapsTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Enforce_GuestTier_TrimsToTen_DroppingOldest()
    {
        var userId = "capguest_" + Guid.NewGuid().ToString("N");
        await SeedSamples(userId, isGuest: true, count: 15); // 0..14, oldest = 0

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        await SampleCaps.EnforceAsync(db, userId, isGuest: true);

        var kept = db.WritingSamples.Where(s => s.UserId == userId)
            .OrderBy(s => s.CreatedAt).Select(s => s.Text).ToList();
        Assert.Equal(10, kept.Count);
        // Oldest five (idx 0..4) dropped; newest ten (5..14) survive.
        Assert.Equal("s5", kept.First());
        Assert.Equal("s14", kept.Last());
    }

    [Fact]
    public async Task Enforce_RegularTier_TrimsToTwoHundred()
    {
        var userId = "capuser_" + Guid.NewGuid().ToString("N");
        await SeedSamples(userId, isGuest: false, count: 205);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        await SampleCaps.EnforceAsync(db, userId, isGuest: false);

        Assert.Equal(200, db.WritingSamples.Count(s => s.UserId == userId));
    }

    [Fact]
    public async Task Paste_Guest_CapsAtTen()
    {
        var (client, _) = await GuestClientAsync();

        // 12 pasted samples, but a guest keeps only the 10 newest.
        for (var i = 0; i < 12; i++)
        {
            var res = await client.PostAsJsonAsync("/api/samples", new { text = $"paste {i}" });
            res.EnsureSuccessStatusCode();
        }

        var list = await client.GetFromJsonAsync<List<SampleDto>>("/api/samples");
        Assert.Equal(10, list!.Count);
    }

    [Fact]
    public async Task Paste_Regular_KeepsAllUnderCap()
    {
        var client = await RegisterClientAsync();

        for (var i = 0; i < 12; i++)
        {
            var res = await client.PostAsJsonAsync("/api/samples", new { text = $"paste {i}" });
            res.EnsureSuccessStatusCode();
        }

        // A password account's cap is 200, so all 12 survive.
        var list = await client.GetFromJsonAsync<List<SampleDto>>("/api/samples");
        Assert.Equal(12, list!.Count);
    }

    // --- infra ---

    private async Task SeedSamples(string userId, bool isGuest, int count)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GameContext>();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, DisplayName = userId, IsGuest = isGuest });
        var baseTime = DateTime.UtcNow.AddMinutes(-count);
        for (var i = 0; i < count; i++)
            db.WritingSamples.Add(new WritingSample
            {
                UserId = userId, Text = $"s{i}", Source = SampleSource.Game, CreatedAt = baseTime.AddMinutes(i)
            });
        await db.SaveChangesAsync();
    }

    private async Task<(HttpClient client, string token)> GuestClientAsync()
    {
        var client = _factory.CreateClient();
        var name = "cg" + Guid.NewGuid().ToString("N")[..12];
        var res = await client.PostAsJsonAsync("/api/auth/guest", new { username = name });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return (client, body.Token);
    }

    private async Task<HttpClient> RegisterClientAsync()
    {
        var client = _factory.CreateClient();
        var name = "cu" + Guid.NewGuid().ToString("N")[..12];
        var reg = await client.PostAsJsonAsync("/api/auth/register",
            new { username = name, displayName = name, password = "Password123" });
        reg.EnsureSuccessStatusCode();
        var body = await reg.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return client;
    }

    private record AuthResponse(string Token, string DisplayName, string Username);
    private record SampleDto(int Id, string Text, string Source, DateTime CreatedAt);
}
