namespace DiscoveryAgent.Auth;

/// <summary>
/// Result of an authentication attempt.
/// </summary>
public record AuthResult(
    bool IsAuthenticated,
    string UserId = "",
    string Email = "",
    string? DisplayName = null,
    string AuthMode = "none"
);

/// <summary>
/// Validates incoming requests based on the configured auth mode.
/// </summary>
public interface IAuthService
{
    string Mode { get; }
    Task<AuthResult> ValidateAsync(HttpContext context);
}
