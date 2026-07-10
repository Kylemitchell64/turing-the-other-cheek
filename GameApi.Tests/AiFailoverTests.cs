using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using GameApi.GameLoop;
using Xunit;

namespace GameApi.Tests;

// Phase 15: the AI provider failover chain. Every call is served by a stub HTTP handler
// (nothing touches the network) and an injectable clock drives the breaker + daily-quota
// boundaries deterministically.
public class AiFailoverTests
{
    private const string Sys = "sys";
    private const string User = "reply ok";

    [Fact]
    public async Task Chain_TriesInOrder_AndSkipsMissingKeys()
    {
        var noKey = Leg("nokey", key: null, _ => Ok("should never run"));   // no key → dropped
        var first = Leg("first", key: "k", _ => Http(500));                  // has key, fails
        var second = Leg("second", key: "k", _ => Ok("second wins"));        // has key, succeeds

        var stats = new AiProviderStats();
        var chain = new AiTextProvider(new[] { noKey.Leg, first.Leg, second.Leg }, stats, NullLogger<AiTextProvider>.Instance);

        // Keyless leg is excluded entirely from the active chain.
        Assert.Equal(new[] { "first", "second" }, chain.ActiveProviders);

        var text = await chain.GenerateAsync(Sys, User, 1.0, 60, CancellationToken.None);

        Assert.Equal("second wins", text);
        Assert.Equal(0, noKey.Handler.Calls);   // never touched
        Assert.Equal(1, first.Handler.Calls);   // attempted, failed
        Assert.Equal(1, second.Handler.Calls);  // attempted, succeeded
    }

    [Fact]
    public async Task AllProvidersFail_ReturnsNull_ForCannedFallback()
    {
        var a = Leg("a", "k", _ => Http(500));
        var b = Leg("b", "k", _ => Http(503));
        var chain = new AiTextProvider(new[] { a.Leg, b.Leg }, new AiProviderStats(), NullLogger<AiTextProvider>.Instance);

        var text = await chain.GenerateAsync(Sys, User, 1.0, 60, CancellationToken.None);

        Assert.Null(text); // GeminiBrain's canned fallback takes over from here
    }

    [Fact]
    public async Task Breaker_OpensAfterThreeFails_ThenRecoversAfterCooldown()
    {
        var clock = new Clock(new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc));
        var only = Leg("only", "k", _ => Http(500));
        var stats = new AiProviderStats();
        var chain = new AiTextProvider(new[] { only.Leg }, stats, NullLogger<AiTextProvider>.Instance, clock.Func);

        // Three consecutive failures open the breaker.
        for (var i = 0; i < 3; i++)
            Assert.Null(await chain.GenerateAsync(Sys, User, 1.0, 60, CancellationToken.None));
        Assert.Equal(3, only.Handler.Calls);
        Assert.True(stats.Snapshot(clock.Now).Single().BreakerOpen);

        // While open, the provider is skipped — no HTTP call is made.
        Assert.Null(await chain.GenerateAsync(Sys, User, 1.0, 60, CancellationToken.None));
        Assert.Equal(3, only.Handler.Calls);

        // After the 5-minute cooldown it is tried again.
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.False(stats.Snapshot(clock.Now).Single().BreakerOpen);
        Assert.Null(await chain.GenerateAsync(Sys, User, 1.0, 60, CancellationToken.None));
        Assert.Equal(4, only.Handler.Calls);
    }

    [Fact]
    public async Task RateLimit_ExhaustsProviderForTheDay_WithoutTrippingBreaker()
    {
        var clock = new Clock(new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc));
        var capped = Leg("capped", "k", _ => Http(429));            // plain 429, no Retry-After
        var backup = Leg("backup", "k", _ => Ok("backup answer"));
        var stats = new AiProviderStats();
        var chain = new AiTextProvider(new[] { capped.Leg, backup.Leg }, stats, NullLogger<AiTextProvider>.Instance, clock.Func);

        // First call: capped 429s (exhausted for the day), backup answers.
        Assert.Equal("backup answer", await chain.GenerateAsync(Sys, User, 1.0, 60, CancellationToken.None));
        var s = stats.Snapshot(clock.Now).First(p => p.Provider == "capped");
        Assert.True(s.ExhaustedForDay);
        Assert.False(s.BreakerOpen);              // plain 429 doesn't open the breaker
        Assert.Equal(0, s.ConsecutiveFailures);

        // Second call same day: capped is skipped entirely (no second HTTP hit).
        Assert.Equal("backup answer", await chain.GenerateAsync(Sys, User, 1.0, 60, CancellationToken.None));
        Assert.Equal(1, capped.Handler.Calls);
        Assert.Equal(2, backup.Handler.Calls);

        // Next UTC day: the daily counter + exhaustion reset, capped is live again.
        clock.Advance(TimeSpan.FromDays(1));
        Assert.False(stats.Snapshot(clock.Now).First(p => p.Provider == "capped").ExhaustedForDay);
    }

    [Fact]
    public async Task RateLimit_WithRetryInfo_CountsTowardBreaker()
    {
        var clock = new Clock(new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc));
        var only = Leg("only", "k", _ => Http(429, retryAfterSeconds: 5));
        var stats = new AiProviderStats();
        var chain = new AiTextProvider(new[] { only.Leg }, stats, NullLogger<AiTextProvider>.Instance, clock.Func);

        await chain.GenerateAsync(Sys, User, 1.0, 60, CancellationToken.None);
        var s = stats.Snapshot(clock.Now).Single();
        Assert.True(s.ExhaustedForDay);
        Assert.Equal(1, s.ConsecutiveFailures); // 429 with retry info also feeds the breaker
    }

    [Fact]
    public async Task Snapshot_CountsRequestsSuccessesAndFailovers()
    {
        var clock = new Clock(new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc));
        var first = Leg("first", "k", _ => Http(500));
        var second = Leg("second", "k", _ => Ok("ok"));
        var stats = new AiProviderStats();
        var chain = new AiTextProvider(new[] { first.Leg, second.Leg }, stats, NullLogger<AiTextProvider>.Instance, clock.Func);

        await chain.GenerateAsync(Sys, User, 1.0, 60, CancellationToken.None);
        await chain.GenerateAsync(Sys, User, 1.0, 60, CancellationToken.None);

        var snap = stats.Snapshot(clock.Now);
        var f = snap.First(p => p.Provider == "first");
        var g = snap.First(p => p.Provider == "second");

        Assert.Equal(2, f.RequestsToday);
        Assert.Equal(2, f.FailureTotal);
        Assert.Equal(2, f.FailoverHops);
        Assert.Equal(2, g.RequestsToday);
        Assert.Equal(2, g.SuccessTotal);
        Assert.Equal(0, g.FailoverHops);
    }

    // --- infra: OpenAiCompatClient legs over stub HTTP handlers ---

    private static (OpenAiCompatClient Leg, StubHandler Handler) Leg(
        string name, string? key, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var factory = new StubHttpClientFactory(handler);
        var settings = new Dictionary<string, string?>();
        if (key != null) settings[name.ToUpperInvariant() + "_API_KEY"] = key;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var leg = new OpenAiCompatClient(
            name, "https://example.test/v1", "test-model", name.ToUpperInvariant() + "_API_KEY",
            QuotaReset.UtcMidnight, factory, config, NullLogger<OpenAiCompatClient>.Instance);
        return (leg, handler);
    }

    private static HttpResponseMessage Ok(string content)
    {
        var body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":"
                   + System.Text.Json.JsonSerializer.Serialize(content) + "}}]}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage Http(int status, int? retryAfterSeconds = null)
    {
        var resp = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        if (retryAfterSeconds is { } s)
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        return resp;
    }

    private sealed class Clock
    {
        public DateTime Now;
        public Clock(DateTime start) => Now = start;
        public Func<DateTime> Func => () => Now;
        public void Advance(TimeSpan by) => Now = Now.Add(by);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int Calls;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
