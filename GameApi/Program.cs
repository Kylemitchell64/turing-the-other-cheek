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

// Render (and most container hosts) inject the listen port via $PORT. When it's set
// and ASPNETCORE_URLS wasn't given explicitly, bind 0.0.0.0:$PORT. The Dockerfile
// leaves ASPNETCORE_URLS unset and defaults to 8080 here so PORT always wins in prod.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    var port = Environment.GetEnvironmentVariable("PORT");
    if (!string.IsNullOrEmpty(port))
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

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

// OAuth (Google + GitHub), feature-flagged: a provider is only "on" when both its
// client id + secret are configured. Plain HTTP code exchange, no provider SDKs.
builder.Services.AddHttpClient("oauth", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<GameApi.Auth.OAuthService>();

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

// Brain selection by config: "Ai:Brain" = "Gemini" | "Mock". Default to Gemini when
// a GEMINI_API_KEY is present, otherwise the Mock (so a keyless run still works).
var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
    ?? builder.Configuration["GEMINI_API_KEY"]
    ?? builder.Configuration["Gemini:ApiKey"];
var brainChoice = builder.Configuration["Ai:Brain"]
    ?? (string.IsNullOrEmpty(geminiKey) ? "Mock" : "Gemini");

// The shared Gemini transport backs both the impostor brain and the style summarizer,
// so the named http client + client are always registered (the summarizer runs even
// under the Mock brain — it just no-ops without a key).
builder.Services.AddHttpClient("gemini", c => c.Timeout = TimeSpan.FromSeconds(6));
builder.Services.AddSingleton<GameApi.GameLoop.GeminiClient>();
builder.Services.AddSingleton<GameApi.GameLoop.StyleSummarizer>();

if (string.Equals(brainChoice, "Gemini", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<GameApi.GameLoop.IAiBrain, GameApi.GameLoop.GeminiBrain>();
}
else
{
    builder.Services.AddSingleton<GameApi.GameLoop.IAiBrain, GameApi.GameLoop.MockBrain>();
}

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

// Serve the built React client from wwwroot so the Docker image is self-contained
// (playable on its own). The API routes and the hub are mapped below and take
// precedence; MapFallbackToFile only catches paths that aren't /api/* or /hubs/*,
// so client-side routes (e.g. /lobby/ABCDE) resolve to index.html.
app.UseDefaultFiles();
app.UseStaticFiles();

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

// SPA fallback: anything not matched by an API controller, the hub, or a static
// file returns index.html so React Router owns client-side navigation. Skipped
// when wwwroot has no index.html (e.g. running the API standalone in dev).
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html")))
    app.MapFallbackToFile("index.html");

app.Run();

// Exposed so the integration test project can spin the app up via WebApplicationFactory.
public partial class Program { }
