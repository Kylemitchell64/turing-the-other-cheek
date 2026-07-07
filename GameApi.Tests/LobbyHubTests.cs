using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace GameApi.Tests;

// Phase 2 gate: two real authed SignalR clients create + join a lobby, both see
// LobbyUpdated, and StartGame with <3 players is rejected. Phase 3's playtest will
// extend this project (4 clients, full round loop).
public class LobbyHubTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public LobbyHubTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task TwoClients_CreateAndJoin_BothSeeRoster()
    {
        var hostToken = await RegisterAsync("host_" + Guid.NewGuid().ToString("N")[..8], "Hostie");
        var guestToken = await RegisterAsync("guest_" + Guid.NewGuid().ToString("N")[..8], "Guesty");

        await using var host = BuildConnection(hostToken);
        await using var guest = BuildConnection(guestToken);

        // Latest LobbyUpdated seen by each client.
        LobbyState? hostState = null;
        LobbyState? guestState = null;
        var hostGotTwo = new TaskCompletionSource();
        var guestGotOne = new TaskCompletionSource();

        host.On<LobbyState>("LobbyUpdated", s =>
        {
            hostState = s;
            if (s.Players.Count == 2) hostGotTwo.TrySetResult();
        });
        guest.On<LobbyState>("LobbyUpdated", s =>
        {
            guestState = s;
            guestGotOne.TrySetResult();
        });

        await host.StartAsync();
        await guest.StartAsync();

        await host.InvokeAsync("CreateLobby");

        // Wait for the host's own LobbyUpdated so we have a code to join with.
        await WaitFor(() => hostState != null, "host never got initial LobbyUpdated");
        var code = hostState!.Code;
        Assert.Equal(5, code.Length);

        await guest.InvokeAsync("JoinLobby", code);

        await Task.WhenAll(
            hostGotTwo.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            guestGotOne.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, hostState!.Players.Count);
        Assert.Equal(2, guestState!.Players.Count);
        Assert.Contains(hostState.Players, p => p.DisplayName == "Hostie" && p.IsHost);
        Assert.Contains(hostState.Players, p => p.DisplayName == "Guesty" && !p.IsHost);
    }

    [Fact]
    public async Task StartGame_WithTwoPlayers_IsRejected()
    {
        var hostToken = await RegisterAsync("host_" + Guid.NewGuid().ToString("N")[..8], "Hostie");
        var guestToken = await RegisterAsync("guest_" + Guid.NewGuid().ToString("N")[..8], "Guesty");

        await using var host = BuildConnection(hostToken);
        await using var guest = BuildConnection(guestToken);

        LobbyState? hostState = null;
        host.On<LobbyState>("LobbyUpdated", s => hostState = s);

        await host.StartAsync();
        await guest.StartAsync();

        await host.InvokeAsync("CreateLobby");
        await WaitFor(() => hostState != null, "host never got initial LobbyUpdated");
        await guest.InvokeAsync("JoinLobby", hostState!.Code);
        await WaitFor(() => hostState!.Players.Count == 2, "guest never showed up");

        // 2 players < 3 required — must throw a HubException surfaced to the caller.
        var ex = await Assert.ThrowsAsync<HubException>(() => host.InvokeAsync("StartGame"));
        Assert.Contains("at least 3", ex.Message);
    }

    [Fact]
    public async Task JoinLobby_BadCode_Throws()
    {
        var token = await RegisterAsync("solo_" + Guid.NewGuid().ToString("N")[..8], "Solo");
        await using var conn = BuildConnection(token);
        await conn.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(() => conn.InvokeAsync("JoinLobby", "ZZZZZ"));
        Assert.Contains("No lobby", ex.Message);
    }

    // --- helpers ---

    private async Task<string> RegisterAsync(string username, string displayName)
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username,
            displayName,
            password = "Password123"
        });
        if (!res.IsSuccessStatusCode)
            throw new Xunit.Sdk.XunitException(
                $"register failed {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var body = await res.Content.ReadFromJsonAsync<AuthResponse>();
        return body!.Token;
    }

    private HubConnection BuildConnection(string token) =>
        new HubConnectionBuilder()
            .WithUrl(_factory.Server.BaseAddress + "hubs/game", options =>
            {
                // TestServer speaks HTTP long-polling over its in-memory handler.
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

    private record AuthResponse(string Token, string DisplayName, string Username);
    private record LobbyState(string Code, string State, List<PlayerEntry> Players);
    private record PlayerEntry(string DisplayName, int TokensRemaining, bool IsConnected, bool IsHost);
}
