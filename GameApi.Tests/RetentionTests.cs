using Microsoft.Extensions.DependencyInjection;
using GameApi.Data;
using GameApi.Models;
using GameApi.Retention;
using Xunit;

namespace GameApi.Tests;

// Phase 13: the daily guest-retention sweep. Deletes only stale guests, carries their
// profile data with them, and — critically — keeps old game transcripts intact by
// nulling the message author instead of deleting the message.
public class RetentionTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public RetentionTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Purge_RemovesOnlyStaleGuests_AndKeepsTranscript()
    {
        var now = DateTime.UtcNow;

        // Unique ids so this test only ever asserts on its own rows (the sweep is global,
        // but every other test's guests are stamped "now" and so are never stale).
        var staleGuestId = "stale_" + Guid.NewGuid().ToString("N");
        var freshGuestId = "fresh_" + Guid.NewGuid().ToString("N");
        var oldRealId = "real_" + Guid.NewGuid().ToString("N");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();

            db.Users.Add(new ApplicationUser
            {
                Id = staleGuestId, UserName = staleGuestId, DisplayName = "StaleGuy",
                IsGuest = true, LastSeenUtc = now.AddDays(-40)
            });
            db.Users.Add(new ApplicationUser
            {
                Id = freshGuestId, UserName = freshGuestId, DisplayName = "FreshGuy",
                IsGuest = true, LastSeenUtc = now.AddDays(-2)
            });
            // A real (password) account idle for a year must NEVER be swept.
            db.Users.Add(new ApplicationUser
            {
                Id = oldRealId, UserName = oldRealId, DisplayName = "RealGuy",
                IsGuest = false, LastSeenUtc = now.AddDays(-365)
            });

            // Profile data belonging to the stale guest — should all go with them.
            db.WritingSamples.Add(new WritingSample { UserId = staleGuestId, Text = "sample", Source = SampleSource.Upload, CreatedAt = now.AddDays(-40) });
            db.StyleProfiles.Add(new StyleProfile { UserId = staleGuestId, SummaryJson = "{}", UpdatedAt = now.AddDays(-40) });
            db.PlayerStats.Add(new PlayerStats { UserId = staleGuestId, GamesPlayed = 3 });

            // A finished game the stale guest played in, with one of their messages.
            var game = new Game { JoinCode = "ZZZZZ", State = GameState.Ended, StartedAt = now.AddDays(-40), EndedAt = now.AddDays(-40) };
            game.Players.Add(new GamePlayer { UserId = staleGuestId, TokensRemaining = 2 });
            game.Messages.Add(new GameMessage
            {
                Round = 1, AuthorUserId = staleGuestId, AuthorDisplayNameAtTime = "StaleGuy",
                Text = "this line must survive", SentAt = now.AddDays(-40)
            });
            db.Games.Add(game);
            await db.SaveChangesAsync();
        }

        var svc = _factory.Services.GetRequiredService<GuestRetentionService>();
        var removed = await svc.PurgeStaleGuestsAsync(now);

        Assert.True(removed >= 1);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();

            // Stale guest and all their profile data gone.
            Assert.False(db.Users.Any(u => u.Id == staleGuestId));
            Assert.False(db.WritingSamples.Any(s => s.UserId == staleGuestId));
            Assert.False(db.StyleProfiles.Any(s => s.UserId == staleGuestId));
            Assert.False(db.PlayerStats.Any(s => s.UserId == staleGuestId));
            Assert.False(db.GamePlayers.Any(p => p.UserId == staleGuestId));

            // Fresh guest and old real account survive.
            Assert.True(db.Users.Any(u => u.Id == freshGuestId));
            Assert.True(db.Users.Any(u => u.Id == oldRealId));

            // The transcript survives: message kept, author nulled, display name intact.
            var msg = db.GameMessages.Single(m => m.Text == "this line must survive");
            Assert.Null(msg.AuthorUserId);
            Assert.Equal("StaleGuy", msg.AuthorDisplayNameAtTime);
        }
    }

    [Fact]
    public async Task Purge_NullLastSeen_IsNotSwept()
    {
        var now = DateTime.UtcNow;
        var id = "nolastseen_" + Guid.NewGuid().ToString("N");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();
            // A guest predating the feature (null LastSeen) is "unknown", not "stale".
            db.Users.Add(new ApplicationUser { Id = id, UserName = id, DisplayName = "Ghost", IsGuest = true, LastSeenUtc = null });
            await db.SaveChangesAsync();
        }

        var svc = _factory.Services.GetRequiredService<GuestRetentionService>();
        await svc.PurgeStaleGuestsAsync(now);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameContext>();
            Assert.True(db.Users.Any(u => u.Id == id));
        }
    }
}
