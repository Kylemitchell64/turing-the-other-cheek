using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using GameApi.Models;

namespace GameApi.Data;

public class GameContext : IdentityDbContext<ApplicationUser>
{
    public GameContext(DbContextOptions<GameContext> options) : base(options) { }

    public DbSet<StyleProfile> StyleProfiles => Set<StyleProfile>();
    public DbSet<WritingSample> WritingSamples => Set<WritingSample>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<GamePlayer> GamePlayers => Set<GamePlayer>();
    public DbSet<GameMessage> GameMessages => Set<GameMessage>();
    public DbSet<PlayerStats> PlayerStats => Set<PlayerStats>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<StyleProfile>(e =>
        {
            e.Property(p => p.SummaryJson).HasColumnType("jsonb");
            e.HasIndex(p => p.UserId).IsUnique();
            e.HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<WritingSample>(e =>
        {
            e.Property(p => p.Text).HasMaxLength(10_000);
            e.HasIndex(p => p.UserId);
            e.HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Game>(e =>
        {
            e.Property(p => p.JoinCode).HasMaxLength(5);
            e.HasIndex(p => p.JoinCode);
        });

        builder.Entity<GamePlayer>(e =>
        {
            e.HasIndex(p => new { p.GameId, p.UserId }).IsUnique();
            e.HasOne(p => p.Game)
                .WithMany(g => g.Players)
                .HasForeignKey(p => p.GameId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<GameMessage>(e =>
        {
            e.Property(p => p.Text).HasMaxLength(280);
            e.HasIndex(p => new { p.GameId, p.Round });
            e.HasOne(p => p.Game)
                .WithMany(g => g.Messages)
                .HasForeignKey(p => p.GameId)
                .OnDelete(DeleteBehavior.Cascade);
            // AuthorUserId is nullable: null == the AI player.
            e.HasOne(p => p.Author)
                .WithMany()
                .HasForeignKey(p => p.AuthorUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<PlayerStats>(e =>
        {
            e.HasIndex(p => p.UserId).IsUnique();
            e.HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
