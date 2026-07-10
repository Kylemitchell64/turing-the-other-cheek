using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace GameApi.Tests;

// Phase 21 — host-managed room music over the hub: SetLobbyMusic is host-only, broadcasts
// LobbyMusicChanged to everyone, works mid-game (it's cosmetic), and a late joiner reads
// the current mood off LobbyStateDto so the whole room stays on one soundtrack.
public class MusicHubTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public MusicHubTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task SetLobbyMusic_HostOnly_Broadcasts_LateJoinerSeesMood()
    {
        var host = await UserClient();
        await using var hostConn = BuildConnection(host.Token);
        LobbyState? hostState = null;
        string? hostMood = null;
        hostConn.On<LobbyState>("LobbyUpdated", s => hostState = s);
        hostConn.On<string>("LobbyMusicChanged", m => hostMood = m);
        await hostConn.StartAsync();
        await hostConn.InvokeAsync("CreateLobby");
        await WaitFor(() => hostState != null, "host never saw the lobby");
        var code = hostState!.Code;
        Assert.Equal("arcade", hostState.MusicMood); // server default

        // A second player joins and follows the broadcast.
        var guest = await UserClient();
        await using var guestConn = BuildConnection(guest.Token);
        string? guestMood = null;
        guestConn.On<string>("LobbyMusicChanged", m => guestMood = m);
        await guestConn.StartAsync();
        await guestConn.InvokeAsync("JoinLobby", code);
        await WaitFor(() => hostState!.Players.Count == 2, "guest never joined");

        // A non-host cannot change the music.
        var ex = await Assert.ThrowsAsync<HubException>(() => guestConn.InvokeAsync("SetLobbyMusic", "hype"));
        Assert.Contains("host", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Garbage mood is rejected.
        await Assert.ThrowsAsync<HubException>(() => hostConn.InvokeAsync("SetLobbyMusic", "polka"));

        // Host sets it — both clients get the broadcast.
        await hostConn.InvokeAsync("SetLobbyMusic", "spooky");
        await WaitFor(() => hostMood == "spooky" && guestMood == "spooky", "music broadcast never landed");

        // A LATE joiner reads the current mood straight off the lobby state.
        var latecomer = await UserClient();
        await using var lateConn = BuildConnection(latecomer.Token);
        LobbyState? lateState = null;
        lateConn.On<LobbyState>("LobbyUpdated", s => lateState = s);
        await lateConn.StartAsync();
        await lateConn.InvokeAsync("JoinLobby", code);
        await WaitFor(() => lateState != null, "latecomer never saw the lobby");
        Assert.Equal("spooky", lateState!.MusicMood);
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

    private record AuthDto(string Token, string DisplayName, string Username, bool IsGuest);
    private record LobbyState(string Code, string State, List<PlayerEntry> Players,
        string PackKey, string Difficulty, string PaceKey, string? CrewName, string? CustomPackName,
        string MusicMood);
    private record PlayerEntry(string DisplayName, int TokensRemaining, bool IsConnected, bool IsHost);
}
