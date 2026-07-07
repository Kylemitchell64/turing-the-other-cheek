using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;

namespace GameApi.Tests;

// Phase 6: /api/samples CRUD — auth required, paste-only, 10k char cap, Source=Upload.
public class SamplesApiTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public SamplesApiTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Samples_RequireAuth()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/api/samples");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Add_List_Delete_RoundTrips()
    {
        var client = await AuthedClientAsync();

        // Empty to start.
        var initial = await client.GetFromJsonAsync<List<SampleDto>>("/api/samples");
        Assert.Empty(initial!);

        // Add one.
        var add = await client.PostAsJsonAsync("/api/samples", new { text = "ngl this is how i actually type lol" });
        add.EnsureSuccessStatusCode();
        var created = await add.Content.ReadFromJsonAsync<SampleDto>();
        Assert.Equal("Upload", created!.Source);
        Assert.Equal("ngl this is how i actually type lol", created.Text);

        // Lists back.
        var list = await client.GetFromJsonAsync<List<SampleDto>>("/api/samples");
        Assert.Single(list!);
        Assert.Equal(created.Id, list![0].Id);

        // Delete it.
        var del = await client.DeleteAsync($"/api/samples/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var after = await client.GetFromJsonAsync<List<SampleDto>>("/api/samples");
        Assert.Empty(after!);
    }

    [Fact]
    public async Task Add_EmptyText_IsRejected()
    {
        var client = await AuthedClientAsync();
        var res = await client.PostAsJsonAsync("/api/samples", new { text = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Add_OverCap_IsTruncatedTo10k()
    {
        var client = await AuthedClientAsync();
        var huge = new string('x', 15_000);
        var res = await client.PostAsJsonAsync("/api/samples", new { text = huge });
        res.EnsureSuccessStatusCode();
        var created = await res.Content.ReadFromJsonAsync<SampleDto>();
        Assert.Equal(10_000, created!.Text.Length);
    }

    [Fact]
    public async Task Delete_OtherUsersSample_IsNotFound()
    {
        var owner = await AuthedClientAsync();
        var add = await owner.PostAsJsonAsync("/api/samples", new { text = "mine, hands off" });
        var created = await add.Content.ReadFromJsonAsync<SampleDto>();

        var stranger = await AuthedClientAsync();
        var del = await stranger.DeleteAsync($"/api/samples/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }

    // --- infra ---

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = _factory.CreateClient();
        var username = "s_" + Guid.NewGuid().ToString("N")[..12];
        var reg = await client.PostAsJsonAsync("/api/auth/register",
            new { username, displayName = username, password = "Password123" });
        reg.EnsureSuccessStatusCode();
        var body = await reg.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body!.Token);
        return client;
    }

    private record SampleDto(int Id, string Text, string Source, DateTime CreatedAt);
    private record AuthResponse(string Token, string DisplayName, string Username);
}
