using Storage.Domain.Common;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Stock;

/// <summary>
/// First Expired, First Out: when units leave, they come from the batch that expires
/// soonest, so what is on the shelf is always the freshest the shop has.
/// </summary>
public static class Fefo
{
    /// <summary>
    /// Available batches in the order they should be emptied: nearest expiry first, goods
    /// that never expire last, and among equals the oldest delivery first.
    /// </summary>
    public static IReadOnlyList<Batch> Order(IEnumerable<Batch> batches) =>
        batches
            .Where(batch => batch.IsAvailable)
            .OrderBy(batch => batch.ExpiryDate is null)
            .ThenBy(batch => batch.ExpiryDate)
            .ThenBy(batch => batch.ReceivedAt)
            .ToArray();

    /// <summary>
    /// Decides how many units to take from each batch to make up <paramref name="quantity"/>.
    /// Nothing is changed; the caller applies the plan.
    /// </summary>
    public static IReadOnlyList<(Batch Batch, int Quantity)> Allocate(IEnumerable<Batch> batches, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);

        var plan = new List<(Batch, int)>();
        var missing = quantity;

        foreach (var batch in Order(batches))
        {
            var taken = Math.Min(batch.RemainingQuantity, missing);
            plan.Add((batch, taken));
            missing -= taken;

            if (missing == 0)
            {
                return plan;
            }
        }

        throw new DomainException(
            DomainErrors.StockInsufficient,
            $"Asked for {quantity} units, only {quantity - missing} in stock.");
    }
}

/// <summary>What a set of batches holds and what it cost.</summary>
public readonly record struct StockValuation(int Quantity, Money Value)
{
    /// <summary>
    /// Weighted average cost of one unit in stock. Derived here, from the batches, rather than
    /// kept on the product: a stored average would be rewritten by every receipt from
    /// whatever it last read.
    /// </summary>
    public Money AverageCost => Quantity == 0
        ? Money.Zero
        : Money.FromCents((long)Math.Round((decimal)Value.Cents / Quantity, MidpointRounding.AwayFromZero));

    public static StockValuation Of(IEnumerable<Batch> batches)
    {
        var available = batches.Where(batch => batch.IsAvailable).ToArray();

        return new StockValuation(
            available.Sum(batch => batch.RemainingQuantity),
            available.Aggregate(Money.Zero, (total, batch) => total + batch.Value));
    }
}
