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

public class GuestLoginRequest
{
    // Validated in the controller (3-20 chars, alphanumeric + underscore) so we can
    // return a friendly single error instead of a model-state blob.
    [Required]
    public string Username { get; set; } = default!;
}

public class AuthResponse
{
    public string Token { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string Username { get; set; } = default!;

    // Tier flags the client reads to steer the UX: guests see a light-profile note +
    // get routed to the quick-play flow; a fresh OAuth account with no chosen name is
    // sent to /choose-username first.
    public bool IsGuest { get; set; }
    public bool NeedsUsername { get; set; }
}

// POST /api/profile/username — a signed-in user (typically fresh OAuth) claims a name.
public class ChooseUsernameRequest
{
    public string Username { get; set; } = default!;
}
