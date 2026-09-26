using System.Security.Claims;
using Storage.Api.Auth;
using Storage.Application.Accounts;

namespace Storage.Api.Endpoints;

/// <summary>What the body of a successful sign-in carries. The refresh token is not here.</summary>
public sealed record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, AccountDto Account);

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous by nature, and rate-limited per address: these are the routes a
        // password-guessing script hammers.
        var auth = app.MapGroup("/api/auth")
            .WithTags("Auth")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy);

        auth.MapPost("/sign-up", async (
            SignUpRequest request,
            AuthService service,
            AuthSettings settings,
            HttpResponse response,
            CancellationToken cancellationToken) =>
        {
            var result = await service.SignUpAsync(request, cancellationToken);
            return TypedResults.Created("/api/me", Respond(result, response, settings));
        });

        auth.MapPost("/sign-in", async (
            SignInRequest request,
            AuthService service,
            AuthSettings settings,
            HttpResponse response,
            CancellationToken cancellationToken) =>
            Respond(await service.SignInAsync(request, cancellationToken), response, settings));

        // No body: the refresh token comes from the cookie, which the page cannot read.
        auth.MapPost("/refresh", async (
            HttpRequest request,
            AuthService service,
            AuthSettings settings,
            HttpResponse response,
            CancellationToken cancellationToken) =>
            Respond(await service.RefreshAsync(RefreshCookie.Read(request), cancellationToken), response, settings));

        auth.MapPost("/sign-out", async (
            HttpRequest request,
            AuthService service,
            AuthSettings settings,
            HttpResponse response,
            CancellationToken cancellationToken) =>
        {
            await service.SignOutAsync(RefreshCookie.Read(request), cancellationToken);
            RefreshCookie.Clear(response, settings);
            return TypedResults.NoContent();
        });

        app.MapGet("/api/me", (
            ClaimsPrincipal user,
            AuthService service,
            CancellationToken cancellationToken) =>
            service.GetAccountAsync(
                Guid.Parse(user.FindFirstValue(StorageClaims.Subject)!),
                Guid.Parse(user.FindFirstValue(StorageClaims.Tenant)!),
                cancellationToken))
            .WithTags("Auth");

        return app;
    }

    private static AuthResponse Respond(AuthResult result, HttpResponse response, AuthSettings settings)
    {
        RefreshCookie.Write(response, result.RefreshToken, result.RefreshTokenExpiresAt, settings);
        return new AuthResponse(result.AccessToken.Value, result.AccessToken.ExpiresAt, result.Account);
    }
}
