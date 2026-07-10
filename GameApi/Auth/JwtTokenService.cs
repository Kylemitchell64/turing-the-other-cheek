using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using GameApi.Dtos;
using GameApi.Models;

namespace GameApi.Auth;

// Mints the app's JWTs + the AuthResponse envelope. Shared by AuthController (login /
// guest / OAuth) and ProfileController (username claim mints a fresh token because the
// display name + needsUsername flag change). Keeps one canonical token shape.
public class JwtTokenService
{
    private readonly IConfiguration _configuration;

    public JwtTokenService(IConfiguration configuration) => _configuration = configuration;

    public AuthResponse BuildAuthResponse(ApplicationUser user) => new()
    {
        Token = GenerateJwt(user),
        DisplayName = user.DisplayName ?? user.UserName!,
        Username = user.UserName!,
        IsGuest = user.IsGuest,
        NeedsUsername = user.NeedsUsername
    };

    public string GenerateJwt(ApplicationUser user)
    {
        var jwtKey = Environment.GetEnvironmentVariable("JWT_KEY")
            ?? _configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("JWT_KEY is not configured");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.UserName!),
            new Claim("displayName", user.DisplayName ?? user.UserName!),
            new Claim("isGuest", user.IsGuest ? "true" : "false"),
            new Claim("needsUsername", user.NeedsUsername ? "true" : "false")
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "turing-api",
            audience: "turing-app",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(4),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
