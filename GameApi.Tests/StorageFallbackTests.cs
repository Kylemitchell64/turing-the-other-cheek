using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using GameApi.Data;
using Xunit;

namespace GameApi.Tests;

// Free-tier resilience (ADR 0010): when Postgres doesn't answer at boot, Program.cs falls
// back to EF InMemory instead of dying, so a paused Supabase project can't take the game
// down. Unlike TestAppFactory this factory does NOT swap the DbContext itself — it points
// the real Npgsql path at a port nothing listens on and lets the production fallback fire.
public class FallbackAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable(
            "JWT_KEY", "test-only-signing-key-that-is-at-least-64-characters-long-000000000");

        // Production so the Development-only UseInMemoryDb escape hatch can't be the reason
        // the app came up in memory.
        builder.UseEnvironment("Production");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope",
                ["Db:ProbeSeconds"] = "2",
                // Never fire the recovery restart inside the test host.
                ["Db:RecoveryProbeSeconds"] = "3600",
                ["Ai:Brain"] = "Mock",
                ["RateLimit:PermitsPerMinute"] = "100000",
            });
        });
    }
}

public class StorageFallbackTests : IClassFixture<FallbackAppFactory>
{
    private readonly FallbackAppFactory _factory;

    public StorageFallbackTests(FallbackAppFactory factory) => _factory = factory;

    [Fact]
    public void BootsOnMemoryStoreWhenPostgresIsUnreachable()
    {
        var mode = _factory.Services.GetRequiredService<StorageMode>();
        Assert.True(mode.IsMemory);
        Assert.Equal(StorageMode.Memory, mode.Current);

        // The watcher that restarts the process onto Postgres is registered only in this mode.
        Assert.NotNull(_factory.Services.GetService<PostgresRecoveryWatcher>());
    }

    [Fact]
    public async Task StatusAndHealthReportMemoryMode()
    {
        var client = _factory.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.Equal("memory", status.GetProperty("storage").GetString());
        Assert.False(status.GetProperty("maintenance").GetBoolean());

        // Health must NOT claim db:true just because the in-memory ping works — the
        // keepalive relies on db:false to flag the paused project.
        var health = await client.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.False(health.GetProperty("db").GetBoolean());
        Assert.Equal("memory", health.GetProperty("storage").GetString());
    }

    [Fact]
    public async Task GuestLoginWorksInMemoryMode()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/guest", new { username = "fallback_guest" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
    }

    [Fact]
    public void ProbeReportsUnreachableFastAndReachableNever()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = StorageMode.ProbePostgres(
            "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope",
            TimeSpan.FromSeconds(2));
        sw.Stop();
        Assert.False(ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"probe took {sw.Elapsed}");

        // Garbage never throws out of the probe either.
        Assert.False(StorageMode.ProbePostgres("this is not a connection string", TimeSpan.FromSeconds(1)));
    }
}
