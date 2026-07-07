using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using GameApi.Data;

namespace GameApi.Tests;

// Boots the real app in-memory, but swaps the Npgsql DbContext for EF InMemory so
// Identity works without a Postgres server. Test-only — production stays Npgsql.
public class TestAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // JWT_KEY must exist before Program.cs reads it.
        Environment.SetEnvironmentVariable(
            "JWT_KEY", "test-only-signing-key-that-is-at-least-64-characters-long-000000000");

        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-only-signing-key-that-is-at-least-64-characters-long-000000000",
                // TestServer sees every request as one IP; raise the limit so the
                // suite's many registrations don't trip the 429.
                ["RateLimit:PermitsPerMinute"] = "1000",
                // Tiny round timings so the full playtest (multiple rounds, veto,
                // cooldown, priority) runs in seconds instead of minutes. All the
                // state-machine logic is identical; only the clocks are compressed.
                ["GameTimings:PromptSeconds"] = "2",
                ["GameTimings:RevealSeconds"] = "2",
                ["GameTimings:AccusationSeconds"] = "3",
                ["GameTimings:VetoSeconds"] = "3",
                ["GameTimings:PrioritySeconds"] = "2",
                ["GameTimings:TickMilliseconds"] = "100",
                ["GameTimings:MaxRounds"] = "8"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Drop the Npgsql-configured context registration and re-add InMemory.
            services.RemoveAll<DbContextOptions<GameContext>>();
            services.RemoveAll<GameContext>();

            services.AddDbContext<GameContext>(o => o.UseInMemoryDatabase("turing-tests"));
        });
    }
}
