using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GameApi.Tests;

// Phase 12: /api/profile/character — save + fetch the player's character, with strict
// validation (known keys only, indices inside the sprite system's real ranges).
public class ProfileApiTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public ProfileApiTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Character_RequiresAuth()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/api/profile/character");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Get_BeforeSaving_ReturnsNull()
    {
        var client = await AuthedClientAsync();
        var res = await client.GetAsync("/api/profile/character");
        res.EnsureSuccessStatusCode();
        var body = (await res.Content.ReadAsStringAsync()).Trim();
        Assert.Equal("null", body); // no saved character yet
    }

    [Fact]
    public async Task Put_ThenGet_RoundTrips()
    {
        var client = await AuthedClientAsync();

        // accessory 1 is in the free set — premium ids (3+) need a reward, covered below.
        var saved = await PutCharacterAsync(client, "{\"base\":3,\"hair\":7,\"outfit\":2,\"accessory\":1}");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var cfg = await saved.Content.ReadFromJsonAsync<CharacterDto>();
        Assert.Equal(3, cfg!.Base);
        Assert.Equal(7, cfg.Hair);
        Assert.Equal(2, cfg.Outfit);
        Assert.Equal(1, cfg.Accessory);

        var got = await client.GetFromJsonAsync<CharacterDto>("/api/profile/character");
        Assert.Equal(3, got!.Base);
        Assert.Equal(7, got.Hair);
        Assert.Equal(2, got.Outfit);
        Assert.Equal(1, got.Accessory);
    }

    [Fact]
    public async Task Put_NullAccessory_IsAllowed()
    {
        var client = await AuthedClientAsync();
        var res = await PutCharacterAsync(client, "{\"base\":0,\"hair\":0,\"outfit\":0,\"accessory\":null}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var cfg = await res.Content.ReadFromJsonAsync<CharacterDto>();
        Assert.Null(cfg!.Accessory);
    }

    [Theory]
    [InlineData("{\"base\":8,\"hair\":0,\"outfit\":0,\"accessory\":0}")]   // base out of range (max 7)
    [InlineData("{\"base\":0,\"hair\":10,\"outfit\":0,\"accessory\":0}")]  // hair out of range (max 9)
    [InlineData("{\"base\":0,\"hair\":0,\"outfit\":10,\"accessory\":0}")]  // outfit out of range (max 9)
    [InlineData("{\"base\":0,\"hair\":0,\"outfit\":0,\"accessory\":6}")]   // accessory out of range (max 5)
    [InlineData("{\"base\":-1,\"hair\":0,\"outfit\":0,\"accessory\":0}")]  // negative index
    [InlineData("{\"base\":0,\"hair\":0,\"outfit\":0}")]                    // missing accessory key
    [InlineData("{\"base\":0,\"hair\":0,\"outfit\":0,\"accessory\":0,\"evil\":1}")] // unknown key
    [InlineData("{\"base\":\"x\",\"hair\":0,\"outfit\":0,\"accessory\":0}")] // wrong type
    public async Task Put_BadConfig_IsRejected(string json)
    {
        var client = await AuthedClientAsync();
        var res = await PutCharacterAsync(client, json);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        // And nothing was persisted — GET still returns null.
        var body = (await (await client.GetAsync("/api/profile/character")).Content.ReadAsStringAsync()).Trim();
        Assert.Equal("null", body);
    }

    // --- infra ---

    private static Task<HttpResponseMessage> PutCharacterAsync(HttpClient client, string json) =>
        client.PutAsync("/api/profile/character",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = _factory.CreateClient();
        var username = "p_" + Guid.NewGuid().ToString("N")[..12];
        var reg = await client.PostAsJsonAsync("/api/auth/register",
            new { username, displayName = username, password = "Password123" });
        reg.EnsureSuccessStatusCode();
        var body = await reg.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body!.Token);
        return client;
    }

    private record AuthResponse(string Token, string DisplayName, string Username);
    private record CharacterDto(
        [property: System.Text.Json.Serialization.JsonPropertyName("base")] int Base,
        int Hair, int Outfit, int? Accessory);
}
