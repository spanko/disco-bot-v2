using System.Text.Json;

namespace DiscoveryAgent.Auth;

/// <summary>
/// Entra ID auth via ACA Easy Auth. ACA handles the full OAuth2/OIDC flow;
/// the app just reads the injected headers:
///
/// - X-MS-CLIENT-PRINCIPAL: Base64-encoded JSON with identity claims
/// - X-MS-CLIENT-PRINCIPAL-NAME: User's display name / UPN
/// - X-MS-CLIENT-PRINCIPAL-ID: Object ID
///
/// No token validation needed — ACA has already validated the token.
/// </summary>
public class EntraAuthService : IAuthService
{
    public string Mode => "entra_external";

    public Task<AuthResult> ValidateAsync(HttpContext context)
    {
        var principalHeader = context.Request.Headers["X-MS-CLIENT-PRINCIPAL"].ToString();

        if (string.IsNullOrEmpty(principalHeader))
            return Task.FromResult(new AuthResult(false));

        try
        {
            var json = Convert.FromBase64String(principalHeader);
            var principal = JsonSerializer.Deserialize<EasyAuthPrincipal>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (principal is null)
                return Task.FromResult(new AuthResult(false));

            var email = principal.Claims
                ?.FirstOrDefault(c => c.Typ is "preferred_username" or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress")
                ?.Val ?? "";

            var name = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].ToString();
            var objectId = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"].ToString();

            return Task.FromResult(new AuthResult(
                IsAuthenticated: true,
                UserId: objectId,
                Email: email,
                DisplayName: string.IsNullOrEmpty(name) ? null : name,
                AuthMode: "entra_external"
            ));
        }
        catch
        {
            return Task.FromResult(new AuthResult(false));
        }
    }

    private record EasyAuthPrincipal(
        string? IdentityProvider,
        string? UserId,
        List<EasyAuthClaim>? Claims
    );

    private record EasyAuthClaim(string Typ, string Val);
}
