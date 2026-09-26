using Microsoft.AspNetCore.Identity;
using Storage.Application.Abstractions;

namespace Storage.Infrastructure.Security;

/// <summary>
/// Password hashing through ASP.NET Core Identity's hasher: PBKDF2 with HMAC-SHA512, a
/// per-password salt and a work factor Microsoft raises between releases.
/// </summary>
/// <remarks>
/// First-party and maintained rather than hand-rolled. Its output carries a format marker,
/// so when the defaults get stronger it reports "needs rehash" for older hashes and the
/// sign-in flow upgrades them transparently. Registered as a singleton: the decoy hash is
/// computed once, at start-up, not on a request someone is timing.
/// </remarks>
public sealed class IdentityPasswordHasher : IPasswordHasher
{
    // The Identity hasher is generic over a user type it never reads.
    private static readonly object Nobody = new();

    private readonly PasswordHasher<object> _hasher = new();
    private readonly string _decoyHash;

    public IdentityPasswordHasher() =>
        _decoyHash = _hasher.HashPassword(Nobody, Guid.NewGuid().ToString("N"));

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return _hasher.HashPassword(Nobody, password);
    }

    public PasswordVerification Verify(string passwordHash, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        ArgumentNullException.ThrowIfNull(password);

        return _hasher.VerifyHashedPassword(Nobody, passwordHash, password) switch
        {
            PasswordVerificationResult.Success => PasswordVerification.Succeeded,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SucceededNeedsRehash,
            _ => PasswordVerification.Failed,
        };
    }

    public void VerifyDecoy(string password) =>
        _hasher.VerifyHashedPassword(Nobody, _decoyHash, password ?? string.Empty);
}
