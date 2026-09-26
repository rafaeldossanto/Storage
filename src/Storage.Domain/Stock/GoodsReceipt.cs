using Storage.Domain.Catalog;
using Storage.Domain.Common;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Stock;

/// <summary>One scanned line of a delivery, as it came in and as it went on the shelf.</summary>
public sealed class ReceiptLine
{
    private ReceiptLine()
    {
        // Driver materialisation.
    }

    internal ReceiptLine(
        Guid productId,
        Gtin gtin,
        int quantity,
        int baseUnits,
        Money packagingCost,
        Money unitCost,
        DateOnly? expiryDate,
        Guid batchId)
    {
        ProductId = productId;
        Gtin = gtin;
        Quantity = quantity;
        BaseUnits = baseUnits;
        PackagingCost = packagingCost;
        UnitCost = unitCost;
        ExpiryDate = expiryDate;
        BatchId = batchId;
    }

    public Guid ProductId { get; private set; }

    /// <summary>The code that was scanned - a can or a twelve-pack.</summary>
    public Gtin Gtin { get; private set; }

    /// <summary>How many of the scanned packaging arrived.</summary>
    public int Quantity { get; private set; }

    /// <summary>The same amount in base units - what went into stock.</summary>
    public int BaseUnits { get; private set; }

    /// <summary>The cost of one scanned packaging, as on the invoice.</summary>
    public Money PackagingCost { get; private set; }

    /// <summary>The cost of one base unit, which is what the batch carries.</summary>
    public Money UnitCost { get; private set; }

    public DateOnly? ExpiryDate { get; private set; }

    public Guid BatchId { get; private set; }

    /// <summary>What this line cost, exactly as invoiced - not rebuilt from the rounded unit cost.</summary>
    public Money Total => PackagingCost * Quantity;
}

/// <summary>
/// A delivery: the document behind the batches it created, kept so the shop can answer
/// "what came in on Tuesday, and from whom" long after the batches are sold.
/// </summary>
public sealed class GoodsReceipt : ITenantScoped
{
    public const int InvoiceNumberMaxLength = 30;
    public const int NoteMaxLength = 200;
    public const int MaxLines = 500;

    // Not readonly: the MongoDB driver fills it when reading and silently skips readonly fields.
    private List<ReceiptLine> _lines = [];

    private GoodsReceipt()
    {
        // Driver materialisation.
    }

    private GoodsReceipt(
        Guid tenantId,
        Guid? supplierId,
        string? invoiceNumber,
        string? note,
        Guid userId,
        DateTimeOffset receivedAt)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        SupplierId = supplierId;
        InvoiceNumber = Optional(invoiceNumber, InvoiceNumberMaxLength);
        Note = Optional(note, NoteMaxLength);
        UserId = userId;
        ReceivedAt = receivedAt;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid? SupplierId { get; private set; }

    /// <summary>The supplier's invoice (nota fiscal) number, to find the paper later.</summary>
    public string? InvoiceNumber { get; private set; }

    public string? Note { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public IReadOnlyList<ReceiptLine> Lines => _lines.AsReadOnly();

    public Money TotalCost => _lines.Aggregate(Money.Zero, (total, line) => total + line.Total);

    public static GoodsReceipt Open(
        Guid tenantId,
        Guid? supplierId,
        string? invoiceNumber,
        string? note,
        Guid userId,
        DateTimeOffset receivedAt) =>
        new(tenantId, supplierId, invoiceNumber, note, userId, receivedAt);

    /// <summary>
    /// Records one scanned line and returns the batch it puts on the shelf.
    /// </summary>
    /// <param name="packagingCost">What one of the scanned packaging cost.</param>
    /// <param name="shopToday">Today on the shop's calendar, to refuse goods already expired.</param>
    public Batch Receive(
        Product product,
        Gtin scanned,
        int quantity,
        Money packagingCost,
        DateOnly? expiryDate,
        DateOnly shopToday)
    {
        ArgumentNullException.ThrowIfNull(product);

        if (product.TenantId != TenantId)
        {
            throw new InvalidOperationException("A receipt cannot take in another shop's product.");
        }

        if (_lines.Count >= MaxLines)
        {
            throw new DomainException(DomainErrors.ReceiptTooManyLines, $"A receipt holds at most {MaxLines} lines.");
        }

        if (quantity < 1)
        {
            throw new DomainException(DomainErrors.ReceiptQuantityInvalid, "Receive at least one unit.");
        }

        if (packagingCost.IsNegative)
        {
            throw new DomainException(DomainErrors.BatchCostNegative, "A cost cannot be negative.");
        }

        if (product.TracksExpiry && expiryDate is null)
        {
            // The whole point of tracking expiry: a batch with no date could never be flagged.
            throw new DomainException(DomainErrors.ReceiptExpiryRequired, $"'{product.Name}' needs an expiry date.");
        }

        if (expiryDate is { } expiry && expiry < shopToday)
        {
            // Almost always a typo in the date. Goods that truly arrived expired are refused
            // at the door, not shelved.
            throw new DomainException(DomainErrors.ReceiptAlreadyExpired, "That expiry date has already passed.");
        }

        var factor = product.BaseUnitsFor(scanned);
        var baseUnits = checked(quantity * factor);
        var unitCost = packagingCost.DividedBy(factor);

        var batch = Batch.Receive(TenantId, product.Id, baseUnits, unitCost, expiryDate, ReceivedAt, Id);

        _lines.Add(new ReceiptLine(product.Id, scanned, quantity, baseUnits, packagingCost, unitCost, expiryDate, batch.Id));

        return batch;
    }

    private static string? Optional(string? value, int maxLength)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maxLength
            ? trimmed
            : throw new DomainException(DomainErrors.ReceiptFieldTooLong, $"This field is limited to {maxLength} characters.");
    }
}
