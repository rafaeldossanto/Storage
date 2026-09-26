using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Storage.Application.Abstractions;

namespace Storage.Api.Auth;

/// <summary>
/// The pass to the sales area: a short JWT handed out for the right PIN, sent back in the
/// <see cref="Header"/> header.
/// </summary>
/// <remarks>
/// Signed with the same key as the access token but for another audience, so neither can
/// stand in for the other: an access token does not open the sales area, and a sales pass
/// is not a login.
/// </remarks>
public sealed class SalesAccessTokens(AuthSettings settings, TimeProvider clock) : ISalesAccessIssuer
{
    public const string Header = "X-Sales-Access";

    /// <summary>After this long the PIN is asked again, like a computer locking itself.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private const string Audience = "storage-sales";

    private readonly JsonWebTokenHandler _handler = new();

    public SalesAccess Issue(Guid tenantId, Guid userId)
    {
        var now = clock.GetUtcNow();
        var expires = now + Lifetime;

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(settings.SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [StorageClaims.Subject] = userId.ToString(),
                [StorageClaims.Tenant] = tenantId.ToString(),
            },
        });

        return new SalesAccess(token, expires);
    }

    /// <summary>Whether the pass is genuine, current, and was issued to this person in this shop.</summary>
    public async Task<bool> IsValidAsync(string? token, Guid tenantId, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = settings.Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = settings.SigningKey,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
        });

        return result.IsValid
            && result.Claims.TryGetValue(StorageClaims.Tenant, out var tenant) && tenant as string == tenantId.ToString()
            && result.Claims.TryGetValue(StorageClaims.Subject, out var subject) && subject as string == userId.ToString();
    }
}
