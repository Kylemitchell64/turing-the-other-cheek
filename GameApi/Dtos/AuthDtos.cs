using System.ComponentModel.DataAnnotations;

namespace GameApi.Dtos;

public class RegisterRequest
{
    [Required, StringLength(50, MinimumLength = 3)]
    public string Username { get; set; } = default!;

    [Required, StringLength(50, MinimumLength = 2)]
    public string DisplayName { get; set; } = default!;

    [Required, StringLength(100, MinimumLength = 6)]
    public string Password { get; set; } = default!;
}

public class LoginRequest
{
    [Required]
    public string Username { get; set; } = default!;

    [Required]
    public string Password { get; set; } = default!;
}

public class AuthResponse
{
    public string Token { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string Username { get; set; } = default!;
}
