using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using GameApi.Auth;
using GameApi.Dtos;
using GameApi.Models;

namespace GameApi.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ILogger<AuthController> _logger;
    private readonly OAuthService _oauth;
    private readonly IWebHostEnvironment _env;
    private readonly JwtTokenService _tokens;
    private readonly IConfiguration _config;

    // 3-20 chars, letters/digits/underscore. Shared by guest login + username generation.
    private static readonly Regex UsernameRegex = new("^[A-Za-z0-9_]{3,20}$", RegexOptions.Compiled);

    public AuthController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILogger<AuthController> logger,
        OAuthService oauth,
        IWebHostEnvironment env,
        JwtTokenService tokens,
        IConfiguration config)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _logger = logger;
        _oauth = oauth;
        _env = env;
        _tokens = tokens;
        _config = config;
    }

    // POST /api/auth/register
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        var existing = await _userManager.FindByNameAsync(req.Username);
        if (existing != null)
            return Conflict(new { error = "That username is taken" });

        var user = new ApplicationUser
        {
            UserName = req.Username,
            DisplayName = req.DisplayName
        };

        var result = await _userManager.CreateAsync(user, req.Password);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => e.Description) });

        _logger.LogInformation("New user registered: {Username}", req.Username);
        return Ok(_tokens.BuildAuthResponse(user));
    }

    // POST /api/auth/login
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var user = await _userManager.FindByNameAsync(req.Username);
        if (user == null)
            return Unauthorized(new { error = "Wrong username or password" });

        var check = await _signInManager.CheckPasswordSignInAsync(user, req.Password, lockoutOnFailure: true);
        if (!check.Succeeded)
            return Unauthorized(new { error = "Wrong username or password" });

        await TouchLastSeenAsync(user);
        return Ok(_tokens.BuildAuthResponse(user));
    }

    // GET /api/auth/me
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await _userManager.FindByIdAsync(userId!);
        if (user == null) return NotFound();

        return Ok(new
        {
            id = user.Id,
            username = user.UserName,
            displayName = user.DisplayName,
            isGuest = user.IsGuest,
            needsUsername = user.NeedsUsername,
            // Admin dashboard gate: a Google account whose email is allowlisted. The client
            // uses this to reveal the /admin route + the Home [ADMIN] link.
            isAdmin = Admin.AdminEmails.IsAdmin(_config, user.Email, user.ExternalProvider)
        });
    }

    // POST /api/auth/guest  { username }
    // Find-or-create a passwordless guest account. Same username later == same account
    // (so the AI keeps learning that player). Names are shared with regular accounts:
    // a name owned by a password/OAuth user is rejected — sign in instead.
    [HttpPost("guest")]
    [AllowAnonymous]
    public async Task<IActionResult> Guest([FromBody] GuestLoginRequest req)
    {
        var username = (req.Username ?? "").Trim();
        if (!UsernameRegex.IsMatch(username))
            return BadRequest(new { error = "username must be 3-20 letters, numbers or underscores" });

        var existing = await _userManager.FindByNameAsync(username);
        if (existing != null)
        {
            if (!existing.IsGuest)
                return Conflict(new { error = "that name is claimed, sign in instead" });

            // Resume the existing guest — same style profile carries over. Bump LastSeen
            // so an active guest is never swept by the retention job.
            await TouchLastSeenAsync(existing);
            return Ok(_tokens.BuildAuthResponse(existing));
        }

        var user = new ApplicationUser
        {
            UserName = username,
            DisplayName = username,
            IsGuest = true,
            LastSeenUtc = DateTime.UtcNow
        };

        // No password: passwordless account. CheckPasswordSignInAsync can never succeed
        // for it, so nobody can "log in" to a guest name with a password.
        var result = await _userManager.CreateAsync(user);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => e.Description) });

        _logger.LogInformation("New guest: {Username}", username);
        return Ok(_tokens.BuildAuthResponse(user));
    }

    // GET /api/auth/providers -> { google: bool, github: bool }
    // Client shows the OAuth buttons only for providers that are actually configured.
    [HttpGet("providers")]
    [AllowAnonymous]
    public IActionResult Providers()
    {
        return Ok(new
        {
            google = _oauth.IsConfigured("google"),
            github = _oauth.IsConfigured("github")
        });
    }

    // GET /api/auth/{provider}/login -> 302 to the provider's consent screen.
    [HttpGet("{provider}/login")]
    [AllowAnonymous]
    public IActionResult OAuthLogin(string provider)
    {
        if (!OAuthService.IsKnownProvider(provider) || !_oauth.IsConfigured(provider))
            return NotFound(new { error = "provider not configured" });

        // CSRF: random state echoed back on callback; stashed in a short-lived cookie
        // (SameSite=Lax so it survives the top-level redirect back from the provider).
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        Response.Cookies.Append("oauth_state", state, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10),
            Path = "/api/auth"
        });

        var url = _oauth.BuildAuthorizeUrl(provider, RedirectUri(provider), state);
        return Redirect(url);
    }

    // GET /api/auth/{provider}/callback?code=..&state=..
    // Exchanges the code, finds-or-creates the user, then hands the SPA a token via a URL
    // fragment: {clientUrl}/auth/callback#token=... (JWT is in-memory in the client).
    [HttpGet("{provider}/callback")]
    [AllowAnonymous]
    public async Task<IActionResult> OAuthCallback(string provider, string? code, string? state, string? error)
    {
        var clientUrl = _oauth.ClientUrl(_env.IsDevelopment());

        if (!OAuthService.IsKnownProvider(provider) || !_oauth.IsConfigured(provider))
            return Redirect($"{clientUrl}/auth/callback#error=provider");

        // Consume the state cookie regardless of outcome.
        var cookieState = Request.Cookies["oauth_state"];
        Response.Cookies.Delete("oauth_state", new CookieOptions { Path = "/api/auth" });

        if (!string.IsNullOrEmpty(error))
            return Redirect($"{clientUrl}/auth/callback#error=denied");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) ||
            string.IsNullOrEmpty(cookieState) || !FixedTimeEquals(state, cookieState))
            return Redirect($"{clientUrl}/auth/callback#error=state");

        var identity = await _oauth.ExchangeAsync(provider, code, RedirectUri(provider), HttpContext.RequestAborted);
        if (identity == null)
            return Redirect($"{clientUrl}/auth/callback#error=exchange");

        var user = await FindOrCreateExternalUserAsync(identity);
        if (user == null)
            return Redirect($"{clientUrl}/auth/callback#error=account");

        var auth = _tokens.BuildAuthResponse(user);
        return Redirect($"{clientUrl}/auth/callback#token={Uri.EscapeDataString(auth.Token)}");
    }

    private async Task<ApplicationUser?> FindOrCreateExternalUserAsync(ExternalIdentity identity)
    {
        var existing = await _userManager.Users.FirstOrDefaultAsync(u =>
            u.ExternalProvider == identity.Provider && u.ExternalId == identity.ExternalId);
        if (existing != null)
        {
            await TouchLastSeenAsync(existing);
            return existing;
        }

        // Brand-new external account: it gets a placeholder UserName (unique, Identity
        // needs one) but is flagged NeedsUsername so the client sends it to
        // /choose-username before anything else — where it can also claim a prior guest.
        var username = await GenerateUniqueUsernameAsync(identity.DisplayNameHint ?? identity.Provider);
        var user = new ApplicationUser
        {
            UserName = username,
            DisplayName = identity.DisplayNameHint ?? username,
            Email = identity.Email,
            IsGuest = false,
            NeedsUsername = true,
            LastSeenUtc = DateTime.UtcNow,
            ExternalProvider = identity.Provider,
            ExternalId = identity.ExternalId
        };

        var result = await _userManager.CreateAsync(user);
        if (!result.Succeeded)
        {
            _logger.LogWarning("OAuth user create failed: {Errors}",
                string.Join("; ", result.Errors.Select(e => e.Description)));
            return null;
        }
        return user;
    }

    // Build a valid, unique username from a provider hint (sanitize to the allowed
    // charset, clamp length, then suffix a few random digits on collision).
    private async Task<string> GenerateUniqueUsernameAsync(string hint)
    {
        var cleaned = Regex.Replace(hint, "[^A-Za-z0-9_]", "");
        if (cleaned.Length < 3) cleaned = "player" + cleaned;
        if (cleaned.Length > 16) cleaned = cleaned[..16];

        if (await _userManager.FindByNameAsync(cleaned) == null)
            return cleaned;

        for (var i = 0; i < 20; i++)
        {
            var candidate = cleaned + RandomNumberGenerator.GetInt32(1000, 9999);
            if (candidate.Length > 20) candidate = candidate[..20];
            if (await _userManager.FindByNameAsync(candidate) == null)
                return candidate;
        }
        // Extremely unlikely fallback.
        return "player" + Guid.NewGuid().ToString("N")[..8];
    }

    private string RedirectUri(string provider) =>
        $"{Request.Scheme}://{Request.Host}/api/auth/{provider.ToLowerInvariant()}/callback";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // Bump the account's last-active timestamp so the guest retention job never sweeps
    // an account that's actually being used. Best-effort: a failed write just means the
    // stamp is a little stale, never blocks the login.
    private async Task TouchLastSeenAsync(ApplicationUser user)
    {
        user.LastSeenUtc = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);
    }
}
