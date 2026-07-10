using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using GameApi.GameLoop;
using Xunit;

namespace GameApi.Tests;

// Phase 19 — the crew GROUP profiler: strict JSON extraction with one retry + keep-old on
// failure (same discipline as the style summarizer), and the notes renderer. The AI HTTP
// call is stubbed so nothing hits the network.
public class GroupProfilerTests
{
    // > 80 chars so it clears the min-transcript gate.
    private const string Transcript =
        "Sam: lol no way\nAlex: cant believe you said that fr\nSam: bruh youre so predictable\nAlex: accuse sam already";

    [Fact]
    public void RenderNotes_BuildsBlock_FromKnownFields()
    {
        var json = "{\"vibe\":\"chaotic and fast\",\"groupSlang\":[\"fr\",\"bruh\"]," +
                   "\"commonTopics\":[\"gaming\"],\"detectionHabits\":\"accuse fast\"}";
        var notes = GroupProfiler.RenderNotes(json);
        Assert.NotNull(notes);
        Assert.Contains("GROUP NOTES", notes!);
        Assert.Contains("chaotic and fast", notes);
        Assert.Contains("fr", notes);
        Assert.Contains("accuse fast", notes);
    }

    [Fact]
    public void RenderNotes_ReturnsNull_OnBlankOrBadJson()
    {
        Assert.Null(GroupProfiler.RenderNotes(null));
        Assert.Null(GroupProfiler.RenderNotes(""));
        Assert.Null(GroupProfiler.RenderNotes("not json"));
    }

    [Fact]
    public async Task BuildProfile_ValidJson_FirstTry()
    {
        var payload = GeminiTextResponse("{\"vibe\":\"deadpan and mean\",\"detectionHabits\":\"bait with meta prompts\"}");
        var (profiler, handler) = Build(_ => Ok(payload));
        var result = await profiler.BuildProfileAsync(Transcript, previousJson: null, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Contains("deadpan", result!);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task BuildProfile_BothParseFail_KeepsOldProfile()
    {
        var (profiler, handler) = Build(_ => Ok(GeminiTextResponse("sorry, no json here")));
        var old = "{\"vibe\":\"old vibe\"}";
        var result = await profiler.BuildProfileAsync(Transcript, previousJson: old, CancellationToken.None);
        Assert.Null(result); // null == caller keeps the old group profile
        Assert.Equal(2, handler.Calls); // one retry
    }

    [Fact]
    public async Task BuildProfile_ThinTranscript_NoPrior_ReturnsNull_NoApiCall()
    {
        var (profiler, handler) = Build(_ => throw new Xunit.Sdk.XunitException("must not call the API"));
        var result = await profiler.BuildProfileAsync("too short", previousJson: null, CancellationToken.None);
        Assert.Null(result);
        Assert.Equal(0, handler.Calls);
    }

    // --- infra (mirrors StyleSummarizerTests' stub Gemini handler) ---

    private static (GroupProfiler, StubHandler) Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var factory = new StubHttpClientFactory(handler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GEMINI_API_KEY"] = "test-key" })
            .Build();

        var gemini = new GeminiClient(factory, config, NullLogger<GeminiClient>.Instance);
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var profiler = new GroupProfiler(gemini, scopeFactory, NullLogger<GroupProfiler>.Instance);
        return (profiler, handler);
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
