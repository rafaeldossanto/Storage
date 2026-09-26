using Storage.Domain.Common;

namespace Storage.Domain.Accounts;

/// <summary>
/// A signed-in device, identified by a refresh token.
/// </summary>
/// <remarks>
/// Only a hash of the token is stored: whoever reads the database cannot sign in with what
/// they find. Each refresh retires the session and starts a new one (rotation), so a token
/// is good for exactly one use. A retired token showing up again means it was copied, and
/// the caller answers by ending every session of that person.
/// </remarks>
public sealed class Session : ITenantScoped
{
    private Session()
    {
        // Driver materialisation.
    }

    private Session(Guid tenantId, Guid userId, string tokenHash, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAt = now;
        ExpiresAt = now + lifetime;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>The session that took over when this one was rotated.</summary>
    public Guid? ReplacedBy { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public static Session Start(User user, string tokenHash, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new Session(user.TenantId, user.Id, tokenHash, now, lifetime);
    }

    public bool IsActive(DateTimeOffset now) => !IsRevoked && ExpiresAt > now;

    /// <summary>
    /// Retires this session in favour of a new one with a fresh token and a fresh lifetime.
    /// </summary>
    public Session Rotate(string newTokenHash, DateTimeOffset now, TimeSpan lifetime)
    {
        // The caller checks first and answers the user; reaching this with a dead session
        // is a bug in that check.
        if (!IsActive(now))
        {
            throw new InvalidOperationException("Only an active session can be rotated.");
        }

        var next = new Session(TenantId, UserId, newTokenHash, now, lifetime);

        RevokedAt = now;
        ReplacedBy = next.Id;

        return next;
    }

    /// <summary>Ends the session. Revoking twice keeps the first moment.</summary>
    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
