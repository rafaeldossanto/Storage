using Storage.Domain.Common;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Pricing;

public enum DiscountType
{
    /// <summary>A share off the price, in basis points: 3000 is 30%.</summary>
    Percentage,

    /// <summary>A fixed amount off, in cents.</summary>
    FixedAmount,

    /// <summary>The product sells for exactly this, in cents - "leve por R$ 5,00".</summary>
    FixedPrice,
}

public enum DiscountTarget
{
    Product,

    /// <summary>The category and everything below it.</summary>
    Category,
}

/// <summary>
/// A promotion the shop runs: what it takes off, on what, and when.
/// </summary>
/// <remarks>
/// A rule aimed at a category reaches every product in its branch, and never its siblings:
/// a rule on Energy drinks leaves Sodas alone, a rule on Beverages reaches both. When
/// several rules reach one product, the most specific one wins - see
/// <see cref="DiscountEngine"/>.
/// </remarks>
public sealed class DiscountRule : ITenantScoped
{
    public const int NameMaxLength = 60;
    public const int FullPercentage = 10_000;
    public const int MaxExpiryWindowDays = 365;

    private DiscountRule()
    {
        // Driver materialisation.
    }

    private DiscountRule(Guid tenantId, string name)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Name = ValidateName(name);
        DaysOfWeek = [];
        Active = true;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public DiscountType Type { get; private set; }

    /// <summary>Basis points for a percentage, cents for the other types.</summary>
    public long Value { get; private set; }

    public DiscountTarget TargetType { get; private set; }

    public Guid TargetId { get; private set; }

    /// <summary>First day the rule applies, on the shop's calendar. Null: already running.</summary>
    public DateOnly? StartsOn { get; private set; }

    /// <summary>Last day the rule applies, inclusive. Null: until switched off.</summary>
    public DateOnly? EndsOn { get; private set; }

    /// <summary>The weekdays it runs on - "energy drink Tuesday". Empty: every day.</summary>
    public DayOfWeek[] DaysOfWeek { get; private set; } = [];

    /// <summary>
    /// When set, the rule only reaches units from batches that expire within this many days
    /// - "30% off what expires this week". The discount then clears the shelf instead of the
    /// loss report doing it.
    /// </summary>
    public int? ExpiringWithinDays { get; private set; }

    /// <summary>Breaks a tie between rules equally specific. Higher wins.</summary>
    public int Priority { get; private set; }

    /// <summary>Whether it may apply on top of another stackable rule.</summary>
    public bool Stackable { get; private set; }

    public bool Active { get; private set; }

    public static DiscountRule Create(
        Guid tenantId,
        string name,
        DiscountType type,
        long value,
        DiscountTarget targetType,
        Guid targetId)
    {
        var rule = new DiscountRule(tenantId, name);
        rule.SetDiscount(type, value);
        rule.AimAt(targetType, targetId);
        return rule;
    }

    public void Rename(string name) => Name = ValidateName(name);

    public void SetDiscount(DiscountType type, long value)
    {
        var valid = type switch
        {
            DiscountType.Percentage => value is > 0 and <= FullPercentage,
            DiscountType.FixedAmount => value > 0,
            DiscountType.FixedPrice => value >= 0,
            _ => false,
        };

        if (!valid)
        {
            throw new DomainException(
                DomainErrors.DiscountValueInvalid,
                $"{value} is not a valid {type} discount.");
        }

        Type = type;
        Value = value;
    }

    public void AimAt(DiscountTarget targetType, Guid targetId)
    {
        if (!Enum.IsDefined(targetType) || targetId == Guid.Empty)
        {
            throw new DomainException(DomainErrors.DiscountTargetInvalid, "A rule needs something to aim at.");
        }

        TargetType = targetType;
        TargetId = targetId;
    }

    public void Schedule(DateOnly? startsOn, DateOnly? endsOn, IEnumerable<DayOfWeek>? daysOfWeek)
    {
        if (startsOn is { } start && endsOn is { } end && end < start)
        {
            throw new DomainException(DomainErrors.DiscountPeriodInvalid, "A rule cannot end before it starts.");
        }

        StartsOn = startsOn;
        EndsOn = endsOn;
        DaysOfWeek = (daysOfWeek ?? []).Distinct().Order().ToArray();
    }

    public void LimitToExpiringWithin(int? days)
    {
        if (days is < 0 or > MaxExpiryWindowDays)
        {
            throw new DomainException(
                DomainErrors.DiscountExpiryWindowInvalid,
                $"The expiry window runs from 0 to {MaxExpiryWindowDays} days.");
        }

        ExpiringWithinDays = days;
    }

    public void SetPriority(int priority) => Priority = priority;

    public void SetStackable(bool stackable) => Stackable = stackable;

    public void Activate() => Active = true;

    public void Deactivate() => Active = false;

    /// <summary>Whether the rule runs on this date, on the shop's calendar.</summary>
    public bool RunsOn(DateOnly shopDate) =>
        Active
        && (StartsOn is null || shopDate >= StartsOn)
        && (EndsOn is null || shopDate <= EndsOn)
        && (DaysOfWeek.Length == 0 || DaysOfWeek.Contains(shopDate.DayOfWeek));

    /// <summary>
    /// Whether the rule reaches units expiring on <paramref name="batchExpiry"/>. A rule
    /// without an expiry window reaches every unit; one with a window never reaches goods
    /// that do not expire.
    /// </summary>
    public bool ReachesBatch(DateOnly? batchExpiry, DateOnly shopDate) =>
        ExpiringWithinDays is not { } window
        || (batchExpiry is { } expiry && expiry >= shopDate && expiry.DayNumber - shopDate.DayNumber <= window);

    /// <summary>How much this rule takes off <paramref name="price"/>. Never more than the price.</summary>
    public Money DiscountOn(Money price) => Type switch
    {
        DiscountType.Percentage => Min(price.Percentage(Value / 100m), price),
        DiscountType.FixedAmount => Min(Money.FromCents(Value), price),
        // A "fixed price" above the current one is not a discount at all.
        DiscountType.FixedPrice => Money.FromCents(Math.Max(price.Cents - Value, 0)),
        _ => Money.Zero,
    };

    private static Money Min(Money a, Money b) => a <= b ? a : b;

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > NameMaxLength)
        {
            throw new DomainException(
                DomainErrors.DiscountNameInvalid,
                $"A rule name is required and limited to {NameMaxLength} characters.");
        }

        return trimmed;
    }
}
