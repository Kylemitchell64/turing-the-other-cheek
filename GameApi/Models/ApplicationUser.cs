using Microsoft.AspNetCore.Identity;

namespace GameApi.Models;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
}
