using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GameApi.GameLoop;
using Xunit;

namespace GameApi.Tests;

// Phase 6: the style summarizer's JSON handling and min-sample gate. The Gemini HTTP
// call is stubbed so nothing touches the network — we assert the strict-JSON extractor,
// the one retry on a first-parse failure, and keep-old-profile on total failure.
public class StyleSummarizerTests
{
    // Enough characters to clear the 80-char min-sample gate.
    private const string RealPool =
        "ngl that movie was mid. cant make it tonight my dogs being weird lol. LMAO no way that happened";

    [Fact]
    public void ExtractJson_PullsFirstBraceToLast()
    {
        var raw = "Sure! Here you go:\n```json\n{\"avgLength\": 28, \"slang\": [\"ngl\"]}\n```\nhope that helps";
        var json = StyleSummarizer.ExtractJson(raw);
        Assert.NotNull(json);
        Assert.Contains("\"avgLength\":28", json!.Replace(" ", ""));
    }

    [Fact]
    public void ExtractJson_ReturnsNull_OnNoJson()
    {
        Assert.Null(StyleSummarizer.ExtractJson("no braces at all here"));
        Assert.Null(StyleSummarizer.ExtractJson("{ not valid json"));
        Assert.Null(StyleSummarizer.ExtractJson(null));
    }

    [Fact]
    public async Task Summarize_UnderMinGate_ReturnsDefaults_NoApiCall()
    {
        var (summarizer, handler) = Build(_ => throw new Xunit.Sdk.XunitException("API must not be called under the gate"));
        var result = await summarizer.SummarizeAsync("too short", previousJson: null, CancellationToken.None);
        Assert.Equal(StyleSummarizer.DefaultsJson, result);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Summarize_ValidJson_FirstTry()
    {
        var payload = GeminiTextResponse("{\"avgLength\":28,\"capitalization\":\"lowercase\",\"slang\":[\"ngl\",\"lmao\"]}");
        var (summarizer, handler) = Build(_ => Ok(payload));
        var result = await summarizer.SummarizeAsync(RealPool, previousJson: null, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Contains("lowercase", result!);
        Assert.Equal(1, handler.Calls); // parsed first try, no retry
    }

    [Fact]
    public async Task Summarize_BadThenGood_RetriesOnce()
    {
        var call = 0;
        var (summarizer, handler) = Build(_ =>
        {
            call++;
            return call == 1
                ? Ok(GeminiTextResponse("sorry i cant do that")) // no JSON → parse fail
                : Ok(GeminiTextResponse("{\"avgLength\":40,\"capitalization\":\"mixed\"}"));
        });
        var result = await summarizer.SummarizeAsync(RealPool, previousJson: null, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Contains("mixed", result!);
        Assert.Equal(2, handler.Calls); // one retry
    }

    [Fact]
    public async Task Summarize_BothParseFail_KeepsOldProfile()
    {
        var (summarizer, handler) = Build(_ => Ok(GeminiTextResponse("no json here either")));
        var old = "{\"avgLength\":99}";
        var result = await summarizer.SummarizeAsync(RealPool, previousJson: old, CancellationToken.None);
        Assert.Null(result); // null == caller keeps the old profile
        Assert.Equal(2, handler.Calls);
    }

    // --- infra: a StyleSummarizer backed by a stub Gemini HTTP handler ---

    private static (StyleSummarizer, StubHandler) Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var factory = new StubHttpClientFactory(handler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GEMINI_API_KEY"] = "test-key" })
            .Build();

        var gemini = new GeminiClient(factory, config, NullLogger<GeminiClient>.Instance);
        // The summarizer's scope factory is unused in these direct SummarizeAsync tests.
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var summarizer = new StyleSummarizer(gemini, scopeFactory, NullLogger<StyleSummarizer>.Instance);
        return (summarizer, handler);
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string GeminiTextResponse(string text)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(text);
        return "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":" + escaped + "}]}}]}";
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
