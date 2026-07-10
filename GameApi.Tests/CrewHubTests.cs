using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace GameApi.Tests;

// Phase 19 — crew lobbies over the hub: CreateCrewLobby seeds from the crew config,
// only members can join, SetLobbyOptions persists back to the crew, and a crew's
// persistent code typed into JoinLobby gives a helpful error (different namespace).
public class CrewHubTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public CrewHubTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task CrewLobby_OnlyMembersCanJoin()
    {
        var owner = await UserClient();
        var crew = await CreateCrew(owner.Client, "Members Only");

        await using var ownerConn = BuildConnection(owner.Token);
        LobbyState? ownerState = null;
        ownerConn.On<LobbyState>("LobbyUpdated", s => ownerState = s);
        await ownerConn.StartAsync();
        await ownerConn.InvokeAsync("CreateCrewLobby", crew.Id);
        await WaitFor(() => ownerState != null, "owner never saw the crew lobby");
        var liveCode = ownerState!.Code;
        Assert.Equal("Members Only", ownerState.CrewName);

        // A non-member is bounced.
        var outsider = await UserClient();
        await using var outsiderConn = BuildConnection(outsider.Token);
        await outsiderConn.StartAsync();
        var ex = await Assert.ThrowsAsync<HubException>(() => outsiderConn.InvokeAsync("JoinLobby", liveCode));
        Assert.Contains("crew", ex.Message, StringComparison.OrdinalIgnoreCase);

        // A member (joined the crew via REST) gets in.
        var member = await UserClient();
        await JoinCrew(member.Client, crew.JoinCode);
        await using var memberConn = BuildConnection(member.Token);
        await memberConn.StartAsync();
        await memberConn.InvokeAsync("JoinLobby", liveCode);
        await WaitFor(() => ownerState!.Players.Count == 2, "member never joined the crew lobby");
    }

    [Fact]
    public async Task CrewLobby_SetOptions_PersistsBackToCrew()
    {
        var owner = await UserClient();
        var crew = await CreateCrew(owner.Client, "Config Keepers");
        Assert.Equal("normal", crew.Difficulty); // default

        await using var conn = BuildConnection(owner.Token);
        LobbyState? state = null;
        conn.On<LobbyState>("LobbyUpdated", s => state = s);
        await conn.StartAsync();
        await conn.InvokeAsync("CreateCrewLobby", crew.Id);
        await WaitFor(() => state != null, "no crew lobby");

        await conn.InvokeAsync("SetLobbyOptions", "trivia", "hard", "flash");

        // Persist-back is fire-and-forget — poll GET mine until the crew row reflects it.
        CrewDto? updated = null;
        await WaitFor(async () =>
        {
            var mine = await owner.Client.GetFromJsonAsync<List<CrewDto>>("/api/crews/mine");
            updated = mine!.FirstOrDefault(c => c.Id == crew.Id);
            return updated is { Difficulty: "hard" };
        }, "crew config never persisted back");

        Assert.Equal("trivia", updated!.PackKey);
        Assert.Equal("hard", updated.Difficulty);
        Assert.Equal("flash", updated.PaceKey);
    }

    [Fact]
    public async Task JoinLobby_WithCrewCode_NoLiveLobby_HelpfulError()
    {
        var owner = await UserClient();
        var crew = await CreateCrew(owner.Client, "No Live Lobby");

        await using var conn = BuildConnection(owner.Token);
        await conn.StartAsync();
        // The crew's persistent code has no live lobby — JoinLobby should point them at crews.
        var ex = await Assert.ThrowsAsync<HubException>(() => conn.InvokeAsync("JoinLobby", crew.JoinCode));
        Assert.Contains("crew code", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- helpers ---

    private async Task<(HttpClient Client, string Token)> UserClient()
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
        return (client, body.Token);
    }

    private static async Task<CrewDto> CreateCrew(HttpClient client, string name)
    {
        var res = await client.PostAsJsonAsync("/api/crews", new { name });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<CrewDto>())!;
    }

    private static async Task JoinCrew(HttpClient client, string code)
    {
        var res = await client.PostAsJsonAsync("/api/crews/join", new { code });
        res.EnsureSuccessStatusCode();
    }

    private HubConnection BuildConnection(string token) =>
        new HubConnectionBuilder()
            .WithUrl(_factory.Server.BaseAddress + "hubs/game", options =>
            {
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
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

    private static async Task WaitFor(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(message);
    }

    private record AuthDto(string Token, string DisplayName, string Username, bool IsGuest);
    private record CrewDto(int Id, string Name, string JoinCode, string PackKey, string Difficulty,
        string PaceKey, int MemberCount, int GamesPlayed, bool IsOwner);
    private record LobbyState(string Code, string State, List<PlayerEntry> Players,
        string PackKey, string Difficulty, string PaceKey, string? CrewName);
    private record PlayerEntry(string DisplayName, int TokensRemaining, bool IsConnected, bool IsHost);
}
