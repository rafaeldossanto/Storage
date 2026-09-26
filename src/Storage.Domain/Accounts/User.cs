using Storage.Domain.Common;

namespace Storage.Domain.Accounts;

public enum UserRole
{
    /// <summary>Signed the shop up. Manages the team; cannot be deactivated.</summary>
    Owner,

    /// <summary>Works in the shop: catalogue, stock, counts.</summary>
    Staff,
}

/// <summary>
/// A person who signs in, always inside exactly one shop.
/// </summary>
public sealed class User : ITimestamped, ITenantScoped
{
    public const int NameMaxLength = 80;
    public const int MaxFailedSignIns = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private User()
    {
        // Driver materialisation.
    }

    private User(Guid tenantId, string name, EmailAddress email, string passwordHash, UserRole role)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Name = ValidateName(name);
        Email = email;
        PasswordHash = RequireHash(passwordHash);
        Role = role;
        Active = true;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public EmailAddress Email { get; private set; }

    /// <summary>Never the password itself - only what the hasher produced from it.</summary>
    public string PasswordHash { get; private set; } = string.Empty;

    public UserRole Role { get; private set; }

    public bool Active { get; private set; }

    public int FailedSignIns { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static User CreateOwner(Guid tenantId, string name, EmailAddress email, string passwordHash) =>
        new(tenantId, name, email, passwordHash, UserRole.Owner);

    public static User CreateStaff(Guid tenantId, string name, EmailAddress email, string passwordHash) =>
        new(tenantId, name, email, passwordHash, UserRole.Staff);

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>
    /// Counts a wrong password. The fifth in a row locks the account for
    /// <see cref="LockoutDuration"/>, which turns guessing a password into years of work.
    /// </summary>
    public void RecordFailedSignIn(DateTimeOffset now)
    {
        FailedSignIns++;

        if (FailedSignIns >= MaxFailedSignIns)
        {
            LockedUntil = now + LockoutDuration;
            FailedSignIns = 0;
        }
    }

    public void RecordSuccessfulSignIn()
    {
        FailedSignIns = 0;
        LockedUntil = null;
    }

    public void ChangePasswordHash(string passwordHash) => PasswordHash = RequireHash(passwordHash);

    public void Rename(string name) => Name = ValidateName(name);

    public void Activate() => Active = true;

    /// <summary>
    /// Stops the person from signing in without deleting them: stock movements and counts
    /// they made still point at them.
    /// </summary>
    public void Deactivate()
    {
        // A shop with no active owner could never manage its own team again.
        if (Role == UserRole.Owner)
        {
            throw new DomainException(
                DomainErrors.OwnerCannotBeDeactivated,
                "The shop owner cannot be deactivated.");
        }

        Active = false;
    }

    public void MarkCreated(DateTimeOffset at)
    {
        CreatedAt = at;
        UpdatedAt = at;
    }

    public void MarkUpdated(DateTimeOffset at) => UpdatedAt = at;

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > NameMaxLength)
        {
            throw new DomainException(
                DomainErrors.UserNameInvalid,
                $"A name is required and limited to {NameMaxLength} characters.");
        }

        return trimmed;
    }

    // An empty hash would match nothing - or, with a careless verifier, everything.
    private static string RequireHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        return passwordHash;
    }
}
