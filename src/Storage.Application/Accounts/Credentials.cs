using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Storage.Application.Errors;

namespace Storage.Application.Accounts;

/// <summary>
/// Password rules, following current NIST guidance: a real minimum length and nothing
/// else. Composition rules ("one symbol, one digit") push people to "Senha@123", which is
/// weaker than a long phrase.
/// </summary>
internal static class PasswordPolicy
{
    public const int MinLength = 8;

    // Bounded so a multi-megabyte "password" cannot make the hasher burn CPU on purpose.
    public const int MaxLength = 128;

    public static string Require(string? password) =>
        password is { Length: >= MinLength and <= MaxLength }
            ? password
            : throw UseCaseException.Invalid(
                ErrorCodes.PasswordInvalid,
                $"A password needs between {MinLength} and {MaxLength} characters.");
}

/// <summary>
/// Refresh tokens: 256 random bits handed to the browser, and only their SHA-256 kept.
/// </summary>
/// <remarks>
/// A plain hash is enough here, unlike passwords: the token is random and long, so there
/// is nothing to guess and no dictionary to run - slowing the hash down would buy nothing.
/// </remarks>
internal static class RefreshTokens
{
    public static (string Token, string Hash) Create()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (token, HashOf(token));
    }

    public static string HashOf(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
