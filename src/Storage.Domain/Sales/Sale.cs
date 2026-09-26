using Storage.Domain.Catalog;
using Storage.Domain.Common;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Sales;

public enum SaleStatus
{
    Completed,
    Cancelled,
}

/// <summary>One product on a sale: how many units, at what price, and what they had cost.</summary>
public sealed class SaleLine
{
    private SaleLine()
    {
        // Driver materialisation.
    }

    internal SaleLine(Guid productId, string productName, int quantity, Money unitPrice, Money cost)
    {
        ProductId = productId;
        ProductName = productName;
        Quantity = quantity;
        UnitPrice = unitPrice;
        Cost = cost;
    }

    public Guid ProductId { get; private set; }

    /// <summary>The name when it was sold, so an old sale still reads right after a rename.</summary>
    public string ProductName { get; private set; } = string.Empty;

    /// <summary>Base units - a twelve-pack sold counts twelve.</summary>
    public int Quantity { get; private set; }

    /// <summary>What one unit was sold for, discounts included.</summary>
    public Money UnitPrice { get; private set; }

    /// <summary>What these units had cost the shop, batch by batch as they left.</summary>
    public Money Cost { get; private set; }

    public Money Revenue => UnitPrice * Quantity;

    /// <summary>What was left after paying for the goods: sold for, minus what they cost.</summary>
    public Money Net => Revenue - Cost;
}

/// <summary>
/// A sale rung up in the shop: what left the shelf, what it was sold for and what it had
/// cost.
/// </summary>
/// <remarks>
/// The cost is not an average worked out later: it is the cost of the very batches the
/// units came from, taken first-expiry-first. That is what makes "what was left" true for
/// this sale, and for the month.
/// </remarks>
public sealed class Sale : ITenantScoped
{
    public const int MaxLines = 200;
    public const int MaxQuantityPerLine = 100_000;

    /// <summary>
    /// How long a sale can be undone. Long enough to catch a wrong scan at the till; short
    /// enough that a sale cannot quietly disappear from the books at the end of the day.
    /// </summary>
    public static readonly TimeSpan CancellationWindow = TimeSpan.FromMinutes(10);

    // Not readonly: the MongoDB driver fills it when reading and silently skips readonly fields.
    private List<SaleLine> _lines = [];

    private Sale()
    {
        // Driver materialisation.
    }

    private Sale(Guid tenantId, Guid userId, DateTimeOffset soldAt)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        UserId = userId;
        SoldAt = soldAt;
        Status = SaleStatus.Completed;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Who rang it up.</summary>
    public Guid UserId { get; private set; }

    public DateTimeOffset SoldAt { get; private set; }

    public SaleStatus Status { get; private set; }

    public Guid? CancelledBy { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>Checked when the sale is cancelled, so it cannot be cancelled twice at once.</summary>
    public long Version { get; private set; }

    public IReadOnlyList<SaleLine> Lines => _lines.AsReadOnly();

    public Money Total => _lines.Aggregate(Money.Zero, (sum, line) => sum + line.Revenue);

    public Money Cost => _lines.Aggregate(Money.Zero, (sum, line) => sum + line.Cost);

    public Money Net => Total - Cost;

    public static Sale Start(Guid tenantId, Guid userId, DateTimeOffset soldAt) => new(tenantId, userId, soldAt);

    /// <summary>
    /// Adds a product to the sale. <paramref name="taken"/> is what left each batch, first
    /// to expire first; the line's cost is what exactly those units had cost.
    /// </summary>
    public void AddLine(Product product, Money unitPrice, IReadOnlyList<(Batch Batch, int Quantity)> taken)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(taken);

        var quantity = taken.Sum(part => part.Quantity);

        if (quantity is < 1 or > MaxQuantityPerLine)
        {
            throw new DomainException(
                DomainErrors.SaleQuantityInvalid, $"A line sells from 1 to {MaxQuantityPerLine} units.");
        }

        if (_lines.Count == MaxLines)
        {
            throw new DomainException(DomainErrors.SaleTooManyLines, $"A sale holds at most {MaxLines} products.");
        }

        var cost = taken.Aggregate(Money.Zero, (sum, part) => sum + part.Batch.UnitCost * part.Quantity);

        _lines.Add(new SaleLine(product.Id, product.Name, quantity, unitPrice, cost));
    }

    /// <summary>Refuses a sale with nothing on it; called once every line is in.</summary>
    public void RequireLines()
    {
        if (_lines.Count == 0)
        {
            throw new DomainException(DomainErrors.SaleEmpty, "A sale needs at least one product.");
        }
    }

    public void Cancel(Guid userId, DateTimeOffset at)
    {
        if (Status != SaleStatus.Completed)
        {
            throw new DomainException(DomainErrors.SaleNotCompleted, $"This sale is {Status}.");
        }

        if (at - SoldAt > CancellationWindow)
        {
            throw new DomainException(
                DomainErrors.SaleCancelWindowClosed,
                $"A sale can be cancelled for {CancellationWindow.TotalMinutes} minutes after it is made.");
        }

        Status = SaleStatus.Cancelled;
        CancelledBy = userId;
        CancelledAt = at;
        Version++;
    }
}
