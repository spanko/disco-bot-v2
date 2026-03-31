using DiscoveryAgent.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;

namespace DiscoveryAgent.Auth;

/// <summary>
/// Magic link auth: operator generates a signed JWT link containing the user's
/// email and optional contextId. The stakeholder clicks the link, the token is
/// validated, and a secure HttpOnly cookie is set for subsequent requests.
///
/// Flow:
/// 1. GT operator calls POST /api/auth/magic-link { email, contextId }
/// 2. API returns a signed URL with ?token=...
/// 3. Stakeholder clicks link → GET /api/auth/verify?token=...
/// 4. Token validated → cookie set → redirect to /
/// 5. Subsequent requests validated via cookie
/// </summary>
public class MagicLinkAuthService : IAuthService
{
    private readonly DiscoveryBotSettings _settings;
    private readonly SigningCredentials _signingCredentials;
    private readonly TokenValidationParameters _validationParams;

    public const string CookieName = "disco-auth";

    public string Mode => "magic_link";

    public MagicLinkAuthService(DiscoveryBotSettings settings)
    {
        _settings = settings;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.JwtSigningKey));
        _signingCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        _validationParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "discovery-bot",
            ValidateAudience = true,
            ValidAudience = "discovery-bot",
            ValidateLifetime = true,
            IssuerSigningKey = key,
            ClockSkew = TimeSpan.FromMinutes(2),
        };
    }

    /// <summary>
    /// Generates a signed JWT token for the given email and context.
    /// </summary>
    public string GenerateToken(string email, string? contextId = null)
    {
        var handler = new JsonWebTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([
                new Claim(ClaimTypes.Email, email),
                new Claim("userId", email),
                new Claim("contextId", contextId ?? "default"),
            ]),
            Expires = DateTime.UtcNow.AddHours(_settings.MagicLinkExpiryHours),
            Issuer = "discovery-bot",
            Audience = "discovery-bot",
            SigningCredentials = _signingCredentials,
        };
        return handler.CreateToken(descriptor);
    }

    /// <summary>
    /// Validates a JWT token string and returns claims if valid.
    /// </summary>
    public async Task<AuthResult> ValidateTokenAsync(string token)
    {
        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(token, _validationParams);

        if (!result.IsValid)
            return new AuthResult(false);

        var email = result.ClaimsIdentity.FindFirst(ClaimTypes.Email)?.Value ?? "";
        var userId = result.ClaimsIdentity.FindFirst("userId")?.Value ?? email;

        return new AuthResult(
            IsAuthenticated: true,
            UserId: userId,
            Email: email,
            AuthMode: "magic_link"
        );
    }

    public async Task<AuthResult> ValidateAsync(HttpContext context)
    {
        // Check cookie first
        if (context.Request.Cookies.TryGetValue(CookieName, out var cookieToken))
            return await ValidateTokenAsync(cookieToken);

        // Check Authorization header
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return await ValidateTokenAsync(authHeader["Bearer ".Length..]);

        return new AuthResult(false);
    }
}
