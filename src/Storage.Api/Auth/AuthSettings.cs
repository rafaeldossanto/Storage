using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Storage.Api.Auth;

/// <summary>The claims the access token carries. Part of the contract with the front end.</summary>
public static class StorageClaims
{
    public const string Subject = "sub";
    public const string Tenant = "tenant_id";
    public const string Role = "role";
    public const string Name = "name";
    public const string Email = "email";
}

/// <summary>
/// Everything needed to issue and check tokens, read once at start-up.
/// </summary>
public sealed record AuthSettings(
    string Issuer,
    string Audience,
    SymmetricSecurityKey SigningKey,
    TimeSpan AccessTokenLifetime,
    TimeSpan RefreshTokenLifetime,
    bool SecureCookies)
{
    /// <summary>HS256 needs a 256-bit key; anything shorter can be brute-forced offline.</summary>
    public const int MinimumKeyBytes = 32;

    /// <param name="allowEphemeralKey">
    /// True in Development and while the build writes the OpenAPI contract. A random key is
    /// then generated at start-up, so sessions do not survive a restart - which is fine on a
    /// developer's machine and would sign every customer out on each deploy in production,
    /// where a missing key therefore stops the application instead.
    /// </param>
    public static AuthSettings From(IConfiguration configuration, bool allowEphemeralKey, bool secureCookies)
    {
        var section = configuration.GetSection("Auth");

        return new AuthSettings(
            Issuer: section["Issuer"] ?? "storage-api",
            Audience: section["Audience"] ?? "storage-front",
            SigningKey: new SymmetricSecurityKey(ReadKey(section["SigningKey"], allowEphemeralKey)),
            AccessTokenLifetime: TimeSpan.FromMinutes(section.GetValue("AccessTokenMinutes", 15)),
            RefreshTokenLifetime: TimeSpan.FromDays(section.GetValue("RefreshTokenDays", 30)),
            SecureCookies: secureCookies);
    }

    private static byte[] ReadKey(string? configured, bool allowEphemeralKey)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return allowEphemeralKey
                ? RandomNumberGenerator.GetBytes(64)
                : throw new InvalidOperationException(
                    "Auth:SigningKey is not configured. Set it through the environment " +
                    "(Auth__SigningKey), never in a committed file.");
        }

        var key = Convert.FromBase64String(configured);

        return key.Length >= MinimumKeyBytes
            ? key
            : throw new InvalidOperationException(
                $"Auth:SigningKey must be at least {MinimumKeyBytes} bytes, base64-encoded.");
    }
}
