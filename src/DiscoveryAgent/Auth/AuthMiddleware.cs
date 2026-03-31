using System.Security.Claims;

namespace DiscoveryAgent.Auth;

/// <summary>
/// Middleware that validates requests using the configured IAuthService.
/// Skips auth for health endpoints, static files, and auth endpoints themselves.
/// Sets HttpContext.User with the authenticated identity.
/// </summary>
public class AuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAuthService _authService;
    private readonly ILogger<AuthMiddleware> _logger;

    private static readonly HashSet<string> SkipPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/health",
        "/health/ready",
    };

    public AuthMiddleware(RequestDelegate next, IAuthService authService, ILogger<AuthMiddleware> logger)
    {
        _next = next;
        _authService = authService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip auth for health, static files, and auth endpoints
        if (SkipPaths.Contains(path) ||
            path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase) ||
            IsStaticFile(path))
        {
            await _next(context);
            return;
        }

        var result = await _authService.ValidateAsync(context);

        if (!result.IsAuthenticated)
        {
            _logger.LogWarning("Unauthenticated request to {Path} (auth mode: {Mode})", path, _authService.Mode);
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Authentication required",
                authMode = _authService.Mode,
            });
            return;
        }

        // Set identity on HttpContext for downstream use
        var claims = new List<Claim>
        {
            new("userId", result.UserId),
            new("authMode", result.AuthMode),
        };
        if (!string.IsNullOrEmpty(result.Email))
            claims.Add(new Claim(ClaimTypes.Email, result.Email));
        if (result.DisplayName is not null)
            claims.Add(new Claim(ClaimTypes.Name, result.DisplayName));

        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, result.AuthMode));

        await _next(context);
    }

    private static bool IsStaticFile(string path) =>
        path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
        path == "/";
}
