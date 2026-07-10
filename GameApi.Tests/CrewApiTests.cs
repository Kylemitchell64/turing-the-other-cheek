using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;

namespace GameApi.Tests;

// Phase 19 — crew REST API: CRUD, caps, guest-403, join by code, ownership transfer,
// disband. Uses the standard in-memory TestAppFactory.
public class CrewApiTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public CrewApiTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Guest_CannotCreateCrew_403()
    {
        var client = await GuestClient("guestcrew");
        var res = await client.PostAsJsonAsync("/api/crews", new { name = "the boys" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Contains("sign in", body!.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonGuest_CreatesCrew_WithPersistentCode()
    {
        var client = await UserClient();
        var crew = await CreateCrew(client, "Poker Night");
        Assert.Equal("Poker Night", crew.Name);
        Assert.Equal(5, crew.JoinCode.Length);
        Assert.True(crew.IsOwner);
        Assert.Equal(1, crew.MemberCount);
        Assert.Equal(0, crew.GamesPlayed);

        // Shows up in GET mine.
        var mine = await client.GetFromJsonAsync<List<CrewDto>>("/api/crews/mine");
        Assert.Contains(mine!, c => c.Id == crew.Id && c.IsOwner);
    }

    [Fact]
    public async Task CreateCrew_RejectsBadName()
    {
        var client = await UserClient();
        var res = await client.PostAsJsonAsync("/api/crews", new { name = "ab" }); // too short
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task OwnerCap_FourthCrew_Rejected()
    {
        var client = await UserClient();
        await CreateCrew(client, "One");
        await CreateCrew(client, "Two");
        await CreateCrew(client, "Three");
        var res = await client.PostAsJsonAsync("/api/crews", new { name = "Four" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Contains("own at most", body!.Error);
    }

    [Fact]
    public async Task JoinByCode_AddsMember_Idempotent()
    {
        var owner = await UserClient();
        var crew = await CreateCrew(owner, "Squad");

        var joiner = await UserClient();
        var joined = await JoinCrew(joiner, crew.JoinCode);
        Assert.Equal(2, joined.MemberCount);
        Assert.False(joined.IsOwner);

        // Joining again is a no-op, still 2 members.
        var again = await JoinCrew(joiner, crew.JoinCode);
        Assert.Equal(2, again.MemberCount);
    }

    [Fact]
    public async Task JoinByCode_UnknownCode_404()
    {
        var client = await UserClient();
        var res = await client.PostAsJsonAsync("/api/crews/join", new { code = "ZZZZZ" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task OwnerLeaves_OwnershipTransfersToOldestMember()
    {
        var owner = await UserClient();
        var crew = await CreateCrew(owner, "Legacy");
        var second = await UserClient();
        await JoinCrew(second, crew.JoinCode);

        // Owner leaves → the second member (oldest remaining) becomes owner.
        var leave = await owner.DeleteAsync($"/api/crews/{crew.Id}");
        leave.EnsureSuccessStatusCode();

        var secondMine = await second.GetFromJsonAsync<List<CrewDto>>("/api/crews/mine");
        var now = Assert.Single(secondMine!, c => c.Id == crew.Id);
        Assert.True(now.IsOwner);
        Assert.Equal(1, now.MemberCount);
    }

    [Fact]
    public async Task LastMemberLeaves_CrewDeleted()
    {
        var owner = await UserClient();
        var crew = await CreateCrew(owner, "Solo");
        var leave = await owner.DeleteAsync($"/api/crews/{crew.Id}");
        leave.EnsureSuccessStatusCode();

        var mine = await owner.GetFromJsonAsync<List<CrewDto>>("/api/crews/mine");
        Assert.DoesNotContain(mine!, c => c.Id == crew.Id);
    }

    [Fact]
    public async Task Disband_OwnerOnly()
    {
        var owner = await UserClient();
        var crew = await CreateCrew(owner, "Owned");
        var member = await UserClient();
        await JoinCrew(member, crew.JoinCode);

        // A non-owner can't disband.
        var forbidden = await member.DeleteAsync($"/api/crews/{crew.Id}?disband=true");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // The owner can — and it's gone for everyone.
        var ok = await owner.DeleteAsync($"/api/crews/{crew.Id}?disband=true");
        ok.EnsureSuccessStatusCode();
        var memberMine = await member.GetFromJsonAsync<List<CrewDto>>("/api/crews/mine");
        Assert.DoesNotContain(memberMine!, c => c.Id == crew.Id);
    }

    [Fact]
    public async Task MembershipCap_Eleventh_Rejected()
    {
        var joiner = await UserClient();
        // 10 crews owned by throwaway users; the joiner joins all 10, the 11th is rejected.
        var codes = new List<string>();
        for (var i = 0; i < 11; i++)
        {
            var owner = await UserClient();
            var crew = await CreateCrew(owner, $"Crew{i}");
            codes.Add(crew.JoinCode);
        }

        for (var i = 0; i < 10; i++)
        {
            var res = await joiner.PostAsJsonAsync("/api/crews/join", new { code = codes[i] });
            res.EnsureSuccessStatusCode();
        }
        var eleventh = await joiner.PostAsJsonAsync("/api/crews/join", new { code = codes[10] });
        Assert.Equal(HttpStatusCode.BadRequest, eleventh.StatusCode);
    }

    // --- helpers ---

    private async Task<HttpClient> UserClient()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "u_" + Guid.NewGuid().ToString("N")[..10],
            displayName = "User" + Guid.NewGuid().ToString("N")[..4],
            password = "Password123"
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<AuthDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return client;
    }

    private async Task<HttpClient> GuestClient(string prefix)
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/guest",
            new { username = prefix + Guid.NewGuid().ToString("N")[..8] });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<AuthDto>();
        Assert.True(body!.IsGuest);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.Token);
        return client;
    }

    private static async Task<CrewDto> CreateCrew(HttpClient client, string name)
    {
        var res = await client.PostAsJsonAsync("/api/crews", new { name });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<CrewDto>())!;
    }

    private static async Task<CrewDto> JoinCrew(HttpClient client, string code)
    {
        var res = await client.PostAsJsonAsync("/api/crews/join", new { code });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<CrewDto>())!;
    }

    private record AuthDto(string Token, string DisplayName, string Username, bool IsGuest);
    private record CrewDto(int Id, string Name, string JoinCode, string PackKey, string Difficulty,
        string PaceKey, int MemberCount, int GamesPlayed, bool IsOwner);
    private record ErrorDto(string Error);
}
