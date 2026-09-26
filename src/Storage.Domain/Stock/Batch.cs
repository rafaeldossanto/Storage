using Storage.Domain.Common;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Stock;

public enum BatchStatus
{
    /// <summary>On the shelf, counted in the balance.</summary>
    Available,

    /// <summary>Every unit went out. Kept: movements still point at it.</summary>
    Depleted,

    /// <summary>Passed its date. What was left became a recorded loss, never a deletion.</summary>
    Expired,

    /// <summary>Held back from sale - a recall, a damaged pallet under review.</summary>
    Blocked,
}

/// <summary>
/// Units of one product that arrived together, with one cost and one expiry date.
/// </summary>
/// <remarks>
/// Every product is kept in batches, including those that never spoil (no expiry date):
/// one mechanism for all stock. The balance is never stored anywhere - it is the sum of
/// what the available batches still hold - so there is no second number to drift away from
/// the first.
/// </remarks>
public sealed class Batch : ITenantScoped
{
    private Batch()
    {
        // Driver materialisation.
    }

    private Batch(
        Guid tenantId,
        Guid productId,
        int quantity,
        Money unitCost,
        DateOnly? expiryDate,
        DateTimeOffset receivedAt,
        Guid? receiptId)
    {
        if (quantity < 1)
        {
            throw new DomainException(DomainErrors.BatchQuantityInvalid, "A batch holds at least one unit.");
        }

        if (unitCost.IsNegative)
        {
            throw new DomainException(DomainErrors.BatchCostNegative, "A cost cannot be negative.");
        }

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        ProductId = productId;
        InitialQuantity = quantity;
        RemainingQuantity = quantity;
        UnitCost = unitCost;
        ExpiryDate = expiryDate;
        ReceivedAt = receivedAt;
        ReceiptId = receiptId;
        Status = BatchStatus.Available;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>The goods receipt that brought it in, if it came from one.</summary>
    public Guid? ReceiptId { get; private set; }

    /// <summary>
    /// The last day the product may be sold, on the shop's calendar. Null for goods that do
    /// not spoil.
    /// </summary>
    public DateOnly? ExpiryDate { get; private set; }

    public int InitialQuantity { get; private set; }

    /// <summary>Base units still in this batch. Zero once depleted or expired.</summary>
    public int RemainingQuantity { get; private set; }

    /// <summary>What one base unit of this batch cost the shop.</summary>
    public Money UnitCost { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public BatchStatus Status { get; private set; }

    public bool IsAvailable => Status == BatchStatus.Available && RemainingQuantity > 0;

    /// <summary>What the units still here cost - the money sitting on the shelf.</summary>
    public Money Value => UnitCost * RemainingQuantity;

    public static Batch Receive(
        Guid tenantId,
        Guid productId,
        int quantity,
        Money unitCost,
        DateOnly? expiryDate,
        DateTimeOffset receivedAt,
        Guid? receiptId = null) =>
        new(tenantId, productId, quantity, unitCost, expiryDate, receivedAt, receiptId);

    /// <summary>
    /// True from the day after the expiry date: a batch "valid until the 18th" can still be
    /// sold all through the 18th.
    /// </summary>
    public bool HasExpiredOn(DateOnly shopDate) => ExpiryDate is { } expiry && shopDate > expiry;

    /// <summary>Removes units - a loss, a return, a count that came up short.</summary>
    public void Take(int quantity)
    {
        // Callers allocate first (FEFO); asking for more than is here is a bug in that step.
        if (quantity < 1 || quantity > RemainingQuantity || Status != BatchStatus.Available)
        {
            throw new InvalidOperationException(
                $"Cannot take {quantity} from a batch holding {RemainingQuantity} ({Status}).");
        }

        RemainingQuantity -= quantity;

        if (RemainingQuantity == 0)
        {
            Status = BatchStatus.Depleted;
        }
    }

    /// <summary>Puts units back - a sale cancelled moments after it was rung up.</summary>
    public void Return(int quantity)
    {
        // Units never leave a batch in larger numbers than it held, so they cannot come
        // back in larger numbers either; asking for that is a bug in the caller.
        if (quantity < 1 || RemainingQuantity + quantity > InitialQuantity)
        {
            throw new InvalidOperationException(
                $"Cannot return {quantity} to a batch holding {RemainingQuantity} of {InitialQuantity}.");
        }

        // An expired batch has already been written off as a loss; units put back into it
        // would never be sold nor counted as lost.
        if (Status == BatchStatus.Expired)
        {
            throw new DomainException(DomainErrors.BatchExpired, "The batch these units came from has expired.");
        }

        RemainingQuantity += quantity;

        if (Status == BatchStatus.Depleted)
        {
            Status = BatchStatus.Available;
        }
    }

    /// <summary>
    /// Takes the batch off sale for having passed its date and returns how many units were
    /// lost - the quantity the loss movement records.
    /// </summary>
    public int Expire()
    {
        if (Status != BatchStatus.Available)
        {
            throw new InvalidOperationException($"Only an available batch can expire, this one is {Status}.");
        }

        var lost = RemainingQuantity;

        RemainingQuantity = 0;
        Status = BatchStatus.Expired;

        return lost;
    }
}
