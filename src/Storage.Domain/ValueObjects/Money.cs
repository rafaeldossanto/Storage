using System.Globalization;

namespace Storage.Domain.ValueObjects;

/// <summary>
/// A monetary amount stored as whole cents.
/// </summary>
/// <remarks>
/// SQLite has no decimal type: REAL loses precision on sums and TEXT sorts
/// lexicographically, so money is persisted as an INTEGER count of cents and only
/// converted to decimal at the edges. Every arithmetic operation here stays in
/// integer space, which means a total can never drift by a fraction of a cent.
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    /// <summary>Retail rounding: 0.005 rounds up to 0.01, which is what a shopkeeper expects.</summary>
    private const MidpointRounding RetailRounding = MidpointRounding.AwayFromZero;

    public static readonly Money Zero = new(0);

    private Money(long cents) => Cents = cents;

    public long Cents { get; }

    public decimal Amount => Cents / 100m;

    public bool IsZero => Cents == 0;

    public bool IsNegative => Cents < 0;

    public static Money FromCents(long cents) => new(cents);

    public static Money FromDecimal(decimal amount) =>
        new((long)Math.Round(amount * 100m, 0, RetailRounding));

    /// <summary>
    /// Applies a percentage and rounds to the nearest cent, e.g. 30% off R$ 8,99 is R$ 2,70.
    /// </summary>
    public Money Percentage(decimal percent) =>
        new((long)Math.Round(Cents * percent / 100m, 0, RetailRounding));

    /// <summary>
    /// Multiplies by a quantity that may itself be fractional (0,350 kg of cheese).
    /// </summary>
    /// <summary>
    /// The share of one unit, rounded to the nearest cent: a pack of 12 that cost R$ 60,00
    /// puts R$ 5,00 on each can. A split that does not come out even loses or gains less than
    /// half a cent per unit.
    /// </summary>
    public Money DividedBy(int units)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(units, 1);
        return new((long)Math.Round((decimal)Cents / units, 0, RetailRounding));
    }

    public Money Times(decimal quantity) =>
        new((long)Math.Round(Cents * quantity, 0, RetailRounding));

    /// <summary>
    /// Splits the amount into <paramref name="parts"/> pieces without losing or inventing
    /// a cent: the remainder is spread one cent at a time over the first pieces. Needed
    /// when a line discount has to be attributed across the batches the items came from.
    /// </summary>
    public Money[] Allocate(int parts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parts, 1);

        var share = Cents / parts;
        var remainder = (int)(Math.Abs(Cents) % parts);
        var step = Cents < 0 ? -1 : 1;

        var result = new Money[parts];
        for (var i = 0; i < parts; i++)
        {
            result[i] = new Money(share + (i < remainder ? step : 0));
        }

        return result;
    }

    /// <summary>
    /// Splits the amount proportionally to <paramref name="weights"/>, giving any leftover
    /// cents to the largest weights first. The pieces always add up to the original amount.
    /// </summary>
    public Money[] AllocateByWeights(IReadOnlyList<decimal> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentOutOfRangeException.ThrowIfZero(weights.Count);

        var totalWeight = weights.Sum();
        if (totalWeight <= 0)
        {
            return Allocate(weights.Count);
        }

        var result = new Money[weights.Count];
        var distributed = 0L;

        for (var i = 0; i < weights.Count; i++)
        {
            var piece = (long)Math.Truncate(Cents * weights[i] / totalWeight);
            result[i] = new Money(piece);
            distributed += piece;
        }

        var leftover = Cents - distributed;
        var order = Enumerable.Range(0, weights.Count)
            .OrderByDescending(i => weights[i])
            .ToArray();

        for (var i = 0; leftover != 0; i = (i + 1) % order.Length)
        {
            var step = leftover > 0 ? 1 : -1;
            result[order[i]] = new Money(result[order[i]].Cents + step);
            leftover -= step;
        }

        return result;
    }

    public static Money operator +(Money left, Money right) => new(left.Cents + right.Cents);

    public static Money operator -(Money left, Money right) => new(left.Cents - right.Cents);

    public static Money operator -(Money value) => new(-value.Cents);

    public static Money operator *(Money left, int quantity) => new(left.Cents * quantity);

    public static Money operator *(int quantity, Money right) => right * quantity;

    public static bool operator <(Money left, Money right) => left.Cents < right.Cents;

    public static bool operator >(Money left, Money right) => left.Cents > right.Cents;

    public static bool operator <=(Money left, Money right) => left.Cents <= right.Cents;

    public static bool operator >=(Money left, Money right) => left.Cents >= right.Cents;

    public int CompareTo(Money other) => Cents.CompareTo(other.Cents);

    /// <summary>
    /// Formats using the ambient culture, which the host pins to pt-BR, so this renders
    /// as "R$ 8,99" on screen without the domain knowing anything about Portuguese.
    /// </summary>
    public override string ToString() => Amount.ToString("C", CultureInfo.CurrentCulture);

    public string ToString(IFormatProvider provider) => Amount.ToString("C", provider);
}
