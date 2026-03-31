using DiscoveryAgent.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;

namespace DiscoveryAgent.Auth;

/// <summary>
/// Invite code auth: each discovery context has a shared code. Users provide the
/// code once, receive a session JWT cookie, and are authenticated for subsequent requests.
///
/// Codes can be configured via:
/// - INVITE_CODES env var: "contextId1:code1,contextId2:code2"
/// - POST /api/auth/invite-codes (runtime management, stored in-memory)
///
/// Flow:
/// 1. User enters invite code on the login page
/// 2. POST /api/auth/validate-code { code }
/// 3. Code matched → session JWT cookie set
/// 4. Subsequent requests validated via cookie
/// </summary>
public class InviteCodeAuthService : IAuthService
{
    private readonly ConcurrentDictionary<string, string> _codes = new(); // code → contextId
    private readonly SigningCredentials? _signingCredentials;
    private readonly TokenValidationParameters? _validationParams;

    public const string CookieName = "disco-auth";

    public string Mode => "invite_code";

    public InviteCodeAuthService(DiscoveryBotSettings settings)
    {
        // Parse INVITE_CODES env var: "contextId:code,contextId2:code2"
        var codesEnv = Environment.GetEnvironmentVariable("INVITE_CODES") ?? "";
        foreach (var pair in codesEnv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':', 2);
            if (parts.Length == 2)
                _codes[parts[1].Trim()] = parts[0].Trim(); // code → contextId
        }

        // If JWT_SIGNING_KEY is set, use it for session cookies
        if (!string.IsNullOrEmpty(settings.JwtSigningKey))
        {
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
    }

    /// <summary>
    /// Validates an invite code. Returns the associated contextId if valid.
    /// </summary>
    public string? ValidateCode(string code) =>
        _codes.TryGetValue(code.Trim(), out var contextId) ? contextId : null;

    /// <summary>
    /// Adds a runtime invite code for a context.
    /// </summary>
    public void AddCode(string contextId, string code) =>
        _codes[code] = contextId;

    /// <summary>
    /// Generates a session JWT after code validation.
    /// </summary>
    public string? GenerateSessionToken(string contextId, string userId)
    {
        if (_signingCredentials is null) return null;

        var handler = new JsonWebTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([
                new Claim("userId", userId),
                new Claim("contextId", contextId),
            ]),
            Expires = DateTime.UtcNow.AddHours(12),
            Issuer = "discovery-bot",
            Audience = "discovery-bot",
            SigningCredentials = _signingCredentials,
        };
        return handler.CreateToken(descriptor);
    }

    public async Task<AuthResult> ValidateAsync(HttpContext context)
    {
        // Check session cookie (JWT)
        if (_validationParams is not null &&
            context.Request.Cookies.TryGetValue(CookieName, out var cookieToken))
        {
            var handler = new JsonWebTokenHandler();
            var result = await handler.ValidateTokenAsync(cookieToken, _validationParams);
            if (result.IsValid)
            {
                var userId = result.ClaimsIdentity.FindFirst("userId")?.Value ?? "invite-user";
                return new AuthResult(
                    IsAuthenticated: true,
                    UserId: userId,
                    AuthMode: "invite_code"
                );
            }
        }

        return new AuthResult(false);
    }
}
