using Storage.Domain.Common;

namespace Storage.Domain.Accounts;

/// <summary>
/// A shop: the customer that subscribes, and the boundary every other record lives inside.
/// </summary>
public sealed class Tenant : ITimestamped
{
    public const int NameMaxLength = 80;

    /// <summary>Where nearly every customer is; changeable per shop.</summary>
    public const string DefaultTimeZoneId = "America/Sao_Paulo";

    /// <summary>Wrong PINs in a row before the sales area locks.</summary>
    public const int SalesPinMaxFailures = 5;

    /// <summary>How long the sales area stays locked after too many wrong PINs.</summary>
    public static readonly TimeSpan SalesPinLockout = TimeSpan.FromMinutes(15);

    private Tenant()
    {
        // Driver materialisation.
    }

    private Tenant(string name, string timeZoneId)
    {
        Id = Guid.CreateVersion7();
        Name = ValidateName(name);
        TimeZoneId = ValidateTimeZone(timeZoneId);
        Active = true;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// IANA id, e.g. "America/Sao_Paulo". The server runs in UTC, but "expires tomorrow" is
    /// a date on the shop's own calendar: a batch that expires on the 18th is still
    /// sellable at 22:00 on the 17th in São Paulo, when it is already the 18th in UTC.
    /// </summary>
    public string TimeZoneId { get; private set; } = DefaultTimeZoneId;

    /// <summary>False when the subscription is suspended: nobody in the shop can sign in.</summary>
    public bool Active { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// The PIN that opens the sales area, hashed like a password. Null until the owner sets
    /// one. Separate from any account's password: it can be handed to a manager without
    /// handing over the owner's account.
    /// </summary>
    public string? SalesPinHash { get; private set; }

    public int SalesPinFailures { get; private set; }

    public DateTimeOffset? SalesPinLockedUntil { get; private set; }

    /// <summary>Bumped by every change to the PIN's state, so concurrent attempts cannot overwrite each other.</summary>
    public long SalesPinVersion { get; private set; }

    public bool HasSalesPin => SalesPinHash is not null;

    public bool IsSalesPinLocked(DateTimeOffset now) => SalesPinLockedUntil > now;

    public void SetSalesPin(string pinHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pinHash);

        SalesPinHash = pinHash;
        SalesPinFailures = 0;
        SalesPinLockedUntil = null;
        SalesPinVersion++;
    }

    /// <summary>
    /// A wrong PIN. A four-digit PIN has ten thousand values, so guessing is stopped the way
    /// a computer's PIN screen stops it: a few tries, then a wait.
    /// </summary>
    public void RecordSalesPinFailure(DateTimeOffset now)
    {
        SalesPinFailures++;

        if (SalesPinFailures >= SalesPinMaxFailures)
        {
            SalesPinFailures = 0;
            SalesPinLockedUntil = now + SalesPinLockout;
        }

        SalesPinVersion++;
    }

    /// <summary>A right PIN clears the count of wrong ones. Returns whether anything changed.</summary>
    public bool RecordSalesPinSuccess()
    {
        if (SalesPinFailures == 0 && SalesPinLockedUntil is null)
        {
            return false;
        }

        SalesPinFailures = 0;
        SalesPinLockedUntil = null;
        SalesPinVersion++;
        return true;
    }

    public static Tenant Create(string name, string? timeZoneId = null) =>
        new(name, timeZoneId ?? DefaultTimeZoneId);

    /// <summary>The calendar date in the shop at a given instant.</summary>
    public DateOnly LocalDate(DateTimeOffset instant)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
    }

    /// <summary>
    /// The instant a calendar day begins in the shop - midnight there, expressed in UTC. A
    /// report on "September" runs from the start of the 1st to the start of 1 October where
    /// the shop is, not in UTC.
    /// </summary>
    public DateTimeOffset StartOf(DateOnly date)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        var utc = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), zone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    public void Rename(string name) => Name = ValidateName(name);

    public void ChangeTimeZone(string timeZoneId) => TimeZoneId = ValidateTimeZone(timeZoneId);

    public void Suspend() => Active = false;

    public void Reactivate() => Active = true;

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
                DomainErrors.ShopNameInvalid,
                $"A shop name is required and limited to {NameMaxLength} characters.");
        }

        return trimmed;
    }

    private static string ValidateTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)
            || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
        {
            throw new DomainException(
                DomainErrors.ShopTimeZoneInvalid,
                $"'{timeZoneId}' is not a known time zone.");
        }

        return timeZoneId;
    }
}
