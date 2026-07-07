using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GameApi.Data;

// Design-time factory so `dotnet ef migrations add` works without a real database.
// The dummy connection string is never used to connect — it only lets EF build the model.
public class GameContextFactory : IDesignTimeDbContextFactory<GameContext>
{
    public GameContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<GameContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=design_time;Username=postgres;Password=postgres")
            .Options;

        return new GameContext(options);
    }
}
