using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GameApi.Data;

// Design-time factory so `dotnet ef migrations add` works without a real database.
// Uses the real connection string from the environment when present (so `database update`
// hits the actual DB), otherwise a dummy that only lets EF build the model.
public class GameContextFactory : IDesignTimeDbContextFactory<GameContext>
{
    public GameContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Port=5432;Database=design_time;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<GameContext>()
            .UseNpgsql(conn)
            .Options;

        return new GameContext(options);
    }
}
