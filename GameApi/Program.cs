using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using GameApi.Data;
using GameApi.HealthChecks;
using GameApi.Hubs;
using GameApi.Lobbies;
using GameApi.Models;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=turing;Username=postgres;Password=postgres";

// Cap request body size to 100KB
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
    o.Limits.MaxRequestBodySize = 100_000);

builder.Services.AddDbContext<GameContext>(options =>
    options.UseNpgsql(connectionString));

// ASP.NET Core Identity — username/password
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(o =>
{
    o.User.RequireUniqueEmail = false;
    o.Password.RequireNonAlphanumeric = false;
    o.Password.RequiredLength = 6;
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
.AddEntityFrameworkStores<GameContext>()
.AddDefaultTokenProviders();

// JWT auth
var jwtKey = Environment.GetEnvironmentVariable("JWT_KEY")
    ?? builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("JWT_KEY env var is required");

builder.Services.AddAuthentication(o =>
{
    o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = "turing-api",
        ValidAudience = "turing-app",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };

    // SignalR can't send Authorization headers on the WebSocket handshake, so the
    // client passes the JWT as ?access_token=... . Pull it off the query for hub
    // requests only (standard SignalR JwtBearer pattern).
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/game"))
                context.Token = accessToken;
            return Task.CompletedTask;
        }
    };
});

// Rate limiter: fixed window 30 req/min per IP. Permit count is config-driven so
// the integration test suite (which hammers /api/auth/register) can raise it.
var rateLimitPermits = builder.Configuration.GetValue<int?>("RateLimit:PermitsPerMinute") ?? 30;
builder.Services.AddRateLimiter(o =>
{
    o.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // The SignalR hub is a persistent transport — long-polling fires many HTTP
        // requests per connection. Rate-limiting it would throttle live gameplay, so
        // it's exempt; hub methods have their own in-lobby guards.
        if (context.Request.Path.StartsWithSegments("/hubs"))
            return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("hub");

        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimitPermits,
                Window = TimeSpan.FromMinutes(1)
            });
    });
    o.RejectionStatusCode = 429;
});

// CORS — origin list from config so a prod domain slots in later
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddScoped<DatabaseHealthCheck>();

// Realtime: SignalR + the process-wide in-memory lobby store.
builder.Services.AddSignalR();
builder.Services.AddSingleton<LobbyStore>();

// Game loop: round timings (config-overridable so tests run fast), the AI brain
// (MockBrain this phase; GeminiBrain lands in phase 4), and the engine — one
// instance serving both DI (the hub injects it) and the hosted tick loop.
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var t = new GameApi.GameLoop.GameTimings();
    cfg.GetSection("GameTimings").Bind(t);
    return t;
});
builder.Services.AddSingleton<GameApi.GameLoop.IAiBrain, GameApi.GameLoop.MockBrain>();
builder.Services.AddSingleton<GameApi.GameLoop.GameEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GameApi.GameLoop.GameEngine>());

builder.Services
    .AddControllers(options => options.ReturnHttpNotAcceptable = true)
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Trust reverse proxy headers (Render) so scheme/host are correct
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseCors("AllowFrontend");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Task.CompletedTask;
    });
    await next();
});

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<GameHub>("/hubs/game");

app.Run();

// Exposed so the integration test project can spin the app up via WebApplicationFactory.
public partial class Program { }
