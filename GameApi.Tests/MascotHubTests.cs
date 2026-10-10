using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using GameApi.Lobbies;
using Xunit;

namespace GameApi.Tests;

// Phase 33 — the menu mascot's lobby moments over the hub. MascotCrashed makes every OTHER
// phone's mascot flinch; PokeMascot tells the room someone poked someone's character.
// Both carry display names only, are capped per user (MascotRateLimiter, keyed on the JWT
// like ADR 0008), and go silent once the game starts — the AI only has a seat from
// StartGame on, so the lobby is the one place a character reacting to a player is safe.
public class MascotHubTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public MascotHubTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task MascotCrashed_ReachesOthersOnly_WithDisplayNameOnly()
    {
        var room = await Room(2);
        try
        {
            var (host, guest) = (room[0], room[1]);
            await host.Conn.InvokeAsync("MascotCrashed");
            await WaitFor(() => guest.Crashes.Count == 1, "guest never saw the crash");
            Assert.Equal(host.Name, guest.Crashes[0]);
            await Task.Delay(300);
            Assert.Empty(host.Crashes); // the crasher's own phone isn't told about itself
        }
        finally { await DisposeAll(room); }
    }

    [Fact]
    public async Task MascotCrashed_IsRateLimitedPerUser()
    {
        var room = await Room(2);
        try
        {
            var (host, guest) = (room[0], room[1]);
            for (var i = 0; i < 8; i++) await host.Conn.InvokeAsync("MascotCrashed"); // spam: no error
            await WaitFor(() => guest.Crashes.Count == 3, "budget not delivered");
            await Task.Delay(400);
            Assert.Equal(3, guest.Crashes.Count); // the rest were dropped

            // the cap is per user: the guest still has a full budget of its own
            await guest.Conn.InvokeAsync("MascotCrashed");
            await WaitFor(() => host.Crashes.Count == 1, "guest's crash was wrongly throttled");
        }
        finally { await DisposeAll(room); }
    }

    [Fact]
    public async Task PokeMascot_BroadcastsNames_IgnoresSelfAndStrangers()
    {
        var room = await Room(2);
        try
        {
            var (host, guest) = (room[0], room[1]);
            await host.Conn.InvokeAsync("PokeMascot", host.Name); // yourself: ignored
            await host.Conn.InvokeAsync("PokeMascot", "nobody-here"); // not in the lobby: ignored
            await host.Conn.InvokeAsync("PokeMascot", guest.Name);
            await WaitFor(() => guest.Pokes.Count == 1 && host.Pokes.Count == 1, "poke never landed");
            Assert.Equal((host.Name, guest.Name), guest.Pokes[0]);
            await Task.Delay(300);
            Assert.Single(guest.Pokes);
        }
        finally { await DisposeAll(room); }
    }

    [Fact]
    public async Task MascotEvents_AreLobbyOnly_SilentOnceTheGameStarts()
    {
        var room = await Room(3);
        try
        {
            var (host, guest) = (room[0], room[1]);
            var started = false;
            host.Conn.On<object>("GameStarted", _ => started = true);
            await host.Conn.InvokeAsync("StartGame");
            await WaitFor(() => started, "game never started");

            await host.Conn.InvokeAsync("MascotCrashed");
            await host.Conn.InvokeAsync("PokeMascot", guest.Name);
            await Task.Delay(500);
            Assert.Empty(guest.Crashes);
            Assert.Empty(guest.Pokes);
        }
        finally { await DisposeAll(room); }
    }

    [Fact]
    public void Limiter_WindowsAreFixedAndPerKind()
    {
        var limiter = new MascotRateLimiter();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++) Assert.True(limiter.TryTake("u1", MascotRateLimiter.Kind.Crash, t0));
        Assert.False(limiter.TryTake("u1", MascotRateLimiter.Kind.Crash, t0.AddSeconds(30)));
        // pokes have their own bucket
        Assert.True(limiter.TryTake("u1", MascotRateLimiter.Kind.Poke, t0.AddSeconds(30)));
        // a new window refills
        Assert.True(limiter.TryTake("u1", MascotRateLimiter.Kind.Crash, t0.AddMinutes(1)));
    }

    // --- helpers ---

    private sealed class Seat
    {
        public required HubConnection Conn { get; init; }
        public required string Name { get; init; }
        public List<string> Crashes { get; } = new();
        public List<(string Poker, string Target)> Pokes { get; } = new();
    }

    // n players seated in one fresh lobby, each listening for the mascot broadcasts.
    private async Task<List<Seat>> Room(int n)
    {
        var seats = new List<Seat>();
        string? code = null;
        for (var i = 0; i < n; i++)
        {
            var name = "Masc" + Guid.NewGuid().ToString("N")[..6];
            var token = await Register(name);
            var conn = BuildConnection(token);
            var seat = new Seat { Conn = conn, Name = name };
            conn.On<string>("MascotCrashed", who => { lock (seat.Crashes) seat.Crashes.Add(who); });
            conn.On<string, string>("MascotPoked", (p, t) => { lock (seat.Pokes) seat.Pokes.Add((p, t)); });
            LobbyState? state = null;
            conn.On<LobbyState>("LobbyUpdated", s => state = s);
            await conn.StartAsync();
            if (code == null)
            {
                await conn.InvokeAsync("CreateLobby");
                await WaitFor(() => state != null, "no lobby");
                code = state!.Code;
            }
            else
            {
                await conn.InvokeAsync("JoinLobby", code);
                await WaitFor(() => state != null && state.Players.Count == i + 1, "join never landed");
            }
            seats.Add(seat);
        }
        return seats;
    }

    private static async Task DisposeAll(List<Seat> seats)
    {
        foreach (var s in seats) await s.Conn.DisposeAsync();
    }

    private async Task<string> Register(string displayName)
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "m_" + Guid.NewGuid().ToString("N")[..10],
            displayName,
            password = "Password123"
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<AuthDto>();
        return body!.Token;
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
    private record LobbyState(string Code, string State, List<PlayerEntry> Players);
    private record PlayerEntry(string DisplayName, bool IsHost);
}
