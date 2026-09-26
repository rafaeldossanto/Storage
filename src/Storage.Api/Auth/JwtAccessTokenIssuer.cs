using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Storage.Application.Abstractions;
using Storage.Domain.Accounts;

namespace Storage.Api.Auth;

/// <summary>
/// Issues the access token: a short-lived JWT carrying the person, their shop and role.
/// </summary>
/// <remarks>
/// Built on <see cref="JsonWebTokenHandler"/>, the current handler - the older
/// JwtSecurityTokenHandler is the legacy path. Issuing and checking share
/// <see cref="ValidationParameters"/>, so the two can never drift apart.
/// </remarks>
public sealed class JwtAccessTokenIssuer(AuthSettings settings, TimeProvider clock) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Issue(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var now = clock.GetUtcNow();
        var expires = now + settings.AccessTokenLifetime;

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(settings.SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [StorageClaims.Subject] = user.Id.ToString(),
                [StorageClaims.Tenant] = user.TenantId.ToString(),
                [StorageClaims.Role] = user.Role.ToString(),
                [StorageClaims.Name] = user.Name,
                [StorageClaims.Email] = user.Email.Value,
            },
        });

        return new AccessToken(token, expires);
    }

    public static TokenValidationParameters ValidationParameters(AuthSettings settings) => new()
    {
        ValidIssuer = settings.Issuer,
        ValidAudience = settings.Audience,
        IssuerSigningKey = settings.SigningKey,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,

        // Pinned: a token cannot talk the server into "alg: none" or another algorithm.
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],

        // Tolerates small clock differences without keeping expired tokens alive for long.
        ClockSkew = TimeSpan.FromSeconds(30),

        NameClaimType = StorageClaims.Name,
        RoleClaimType = StorageClaims.Role,
    };
}
