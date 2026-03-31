namespace DiscoveryAgent.Auth;

/// <summary>
/// No authentication — all requests pass through.
/// Used for local development and testing.
/// </summary>
public class NoneAuthService : IAuthService
{
    public string Mode => "none";

    public Task<AuthResult> ValidateAsync(HttpContext context)
    {
        return Task.FromResult(new AuthResult(
            IsAuthenticated: true,
            UserId: "anonymous",
            AuthMode: "none"
        ));
    }
}
