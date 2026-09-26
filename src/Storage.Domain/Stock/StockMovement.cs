using Storage.Domain.Common;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Stock;

public enum MovementType
{
    Receipt,
    ExpiryLoss,
    DamageLoss,
    CountAdjustment,
    ReturnToSupplier,

    /// <summary>Units sold. The sale's own document keeps the price charged.</summary>
    Sale,

    /// <summary>Units of a cancelled sale going back to the batches they left.</summary>
    SaleCancellation,

    /// <summary>Units of a receipt taken back moments after it was entered - typed wrong.</summary>
    ReceiptCancellation,
}

/// <summary>
/// One line of the stock ledger: so many units of one batch came in or went out, and why.
/// </summary>
/// <remarks>
/// Append-only. A movement is never edited and never deleted - a mistake is corrected by a
/// new movement in the other direction. That is what lets the loss report say exactly what
/// expired in March, years later, and what makes the stock auditable at all.
/// </remarks>
public sealed class StockMovement : ITenantScoped
{
    private StockMovement()
    {
        // Driver materialisation.
    }

    private StockMovement(
        Batch batch,
        MovementType type,
        int quantity,
        DateTimeOffset occurredAt,
        Guid? userId,
        Guid? documentId,
        string? note)
    {
        Id = Guid.CreateVersion7();
        TenantId = batch.TenantId;
        ProductId = batch.ProductId;
        BatchId = batch.Id;
        Type = type;
        Quantity = quantity;
        UnitCost = batch.UnitCost;
        OccurredAt = occurredAt;
        UserId = userId;
        DocumentId = documentId;
        Note = note;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid BatchId { get; private set; }

    public MovementType Type { get; private set; }

    /// <summary>Signed: positive when units came in, negative when they went out.</summary>
    public int Quantity { get; private set; }

    /// <summary>The batch's unit cost, copied so the ledger stays true on its own.</summary>
    public Money UnitCost { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Who did it. Null when the system did - an expiry recorded by the daily job.</summary>
    public Guid? UserId { get; private set; }

    /// <summary>The receipt, count or other document behind the movement, if any.</summary>
    public Guid? DocumentId { get; private set; }

    public string? Note { get; private set; }

    /// <summary>Signed cost of the movement: what came in, or what was lost.</summary>
    public Money Value => UnitCost * Quantity;

    public static StockMovement Receipt(Batch batch, DateTimeOffset at, Guid? userId, Guid? receiptId)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return new StockMovement(batch, MovementType.Receipt, batch.InitialQuantity, at, userId, receiptId, note: null);
    }

    public static StockMovement ExpiryLoss(Batch batch, int lostQuantity, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentOutOfRangeException.ThrowIfLessThan(lostQuantity, 1);

        return new StockMovement(batch, MovementType.ExpiryLoss, -lostQuantity, at, userId: null, documentId: null, note: null);
    }

    /// <summary>Units a count found on the shelf that the ledger did not know about.</summary>
    public static StockMovement Adjustment(Batch batch, DateTimeOffset at, Guid? userId, Guid? countId, string? note)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return new StockMovement(batch, MovementType.CountAdjustment, batch.InitialQuantity, at, userId, countId, note);
    }

    /// <summary>Units of a cancelled sale going back into the batch they were sold from.</summary>
    public static StockMovement SaleReturn(Batch batch, int quantity, DateTimeOffset at, Guid? userId, Guid saleId)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);

        return new StockMovement(batch, MovementType.SaleCancellation, quantity, at, userId, saleId, note: null);
    }

    /// <summary>Units leaving a batch for any reason other than expiry.</summary>
    public static StockMovement Outflow(
        MovementType type,
        Batch batch,
        int quantity,
        DateTimeOffset at,
        Guid? userId,
        Guid? documentId = null,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);

        if (type is MovementType.Receipt or MovementType.ExpiryLoss)
        {
            throw new ArgumentException($"{type} has its own factory.", nameof(type));
        }

        return new StockMovement(batch, type, -quantity, at, userId, documentId, note);
    }
}
