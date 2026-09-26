using Storage.Domain.Common;

namespace Storage.Domain.Stock;

public enum StockCountStatus
{
    Open,
    Closed,
    Cancelled,
}

/// <summary>One product as counted on the shelf, and - once closed - how that compared.</summary>
public sealed class CountedItem
{
    public const int MaxQuantity = 1_000_000;

    private CountedItem()
    {
        // Driver materialisation.
    }

    public CountedItem(Guid productId, int countedQuantity)
    {
        ProductId = productId;
        CountedQuantity = ValidateQuantity(countedQuantity);
    }

    public Guid ProductId { get; private set; }

    public int CountedQuantity { get; private set; }

    /// <summary>What the ledger said at the moment the count was closed.</summary>
    public int? SystemQuantity { get; private set; }

    /// <summary>Counted minus system: negative when units went missing.</summary>
    public int? Difference { get; private set; }

    public static int ValidateQuantity(int quantity) =>
        quantity is >= 0 and <= MaxQuantity
            ? quantity
            : throw new DomainException(DomainErrors.CountQuantityInvalid, $"A counted quantity runs from 0 to {MaxQuantity}.");

    internal void Set(int quantity) => CountedQuantity = ValidateQuantity(quantity);

    internal void Add(int units) => CountedQuantity = ValidateQuantity(CountedQuantity + units);

    internal void Settle(int systemQuantity)
    {
        SystemQuantity = systemQuantity;
        Difference = CountedQuantity - systemQuantity;
    }
}

/// <summary>A difference found when a count was closed, to be turned into stock movements.</summary>
public sealed record CountDifference(Guid ProductId, int Counted, int System)
{
    public int Difference => Counted - System;
}

/// <summary>
/// A stock count: people walk the shelves, scan what is there, and closing the count brings
/// the ledger in line with what was found.
/// </summary>
/// <remarks>
/// Counted quantities are written item by item with atomic database updates as the scans
/// come in, so several people counting at once never overwrite each other. The version
/// increases with every update; closing checks it has not moved since the count was read,
/// so a scan that lands between reading and closing is never silently dropped.
/// </remarks>
public sealed class StockCount : ITenantScoped
{
    public const int JustificationMaxLength = 300;

    // Not readonly: the MongoDB driver fills it when reading and silently skips readonly fields.
    private List<CountedItem> _items = [];

    private StockCount()
    {
        // Driver materialisation.
    }

    private StockCount(Guid tenantId, Guid? categoryId, Guid userId, DateTimeOffset startedAt)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        CategoryId = categoryId;
        StartedBy = userId;
        StartedAt = startedAt;
        Status = StockCountStatus.Open;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>The branch being counted. Null for the whole shop.</summary>
    public Guid? CategoryId { get; private set; }

    public Guid StartedBy { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public StockCountStatus Status { get; private set; }

    public Guid? ClosedBy { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>Why the shelf and the ledger disagreed. Required whenever they did.</summary>
    public string? Justification { get; private set; }

    /// <summary>Bumped by every counted update, and checked when closing.</summary>
    public long Version { get; private set; }

    public IReadOnlyList<CountedItem> Items => _items.AsReadOnly();

    public static StockCount Start(Guid tenantId, Guid? categoryId, Guid userId, DateTimeOffset startedAt) =>
        new(tenantId, categoryId, userId, startedAt);

    /// <summary>
    /// Records how many of a product were counted. The database store does the same as one
    /// atomic update per item; this is that rule for code holding the count in memory.
    /// </summary>
    public void SetCounted(Guid productId, int quantity)
    {
        RequireOpen();

        var item = _items.Find(existing => existing.ProductId == productId);
        if (item is null)
        {
            _items.Add(new CountedItem(productId, quantity));
        }
        else
        {
            item.Set(quantity);
        }

        Version++;
    }

    /// <summary>Adds scanned units to a product's count.</summary>
    public void AddCounted(Guid productId, int units)
    {
        RequireOpen();

        var item = _items.Find(existing => existing.ProductId == productId);
        if (item is null)
        {
            _items.Add(new CountedItem(productId, units));
        }
        else
        {
            item.Add(units);
        }

        Version++;
    }

    /// <summary>
    /// Closes the count against what the ledger holds now, and returns every product whose
    /// shelf and ledger disagree.
    /// </summary>
    public IReadOnlyList<CountDifference> Close(
        IReadOnlyDictionary<Guid, int> systemQuantities,
        string? justification,
        Guid userId,
        DateTimeOffset closedAt)
    {
        ArgumentNullException.ThrowIfNull(systemQuantities);
        RequireOpen();

        if (_items.Count == 0)
        {
            throw new DomainException(DomainErrors.CountEmpty, "Nothing was counted.");
        }

        foreach (var item in _items)
        {
            item.Settle(systemQuantities.GetValueOrDefault(item.ProductId));
        }

        var differences = _items
            .Where(item => item.Difference != 0)
            .Select(item => new CountDifference(item.ProductId, item.CountedQuantity, item.SystemQuantity!.Value))
            .ToArray();

        var reason = justification?.Trim();

        // An adjustment without a reason is how shrinkage gets hidden: every correction to
        // the ledger says why it was made.
        if (differences.Length > 0 && string.IsNullOrEmpty(reason))
        {
            throw new DomainException(DomainErrors.CountJustificationRequired, "Explain why the count differs.");
        }

        if (reason is { Length: > JustificationMaxLength })
        {
            throw new DomainException(
                DomainErrors.CountJustificationRequired, $"A justification is limited to {JustificationMaxLength} characters.");
        }

        Status = StockCountStatus.Closed;
        ClosedBy = userId;
        ClosedAt = closedAt;
        Justification = string.IsNullOrEmpty(reason) ? null : reason;

        return differences;
    }

    public void Cancel(Guid userId, DateTimeOffset at)
    {
        RequireOpen();

        Status = StockCountStatus.Cancelled;
        ClosedBy = userId;
        ClosedAt = at;
    }

    private void RequireOpen()
    {
        if (Status != StockCountStatus.Open)
        {
            throw new DomainException(DomainErrors.CountNotOpen, $"This count is {Status}.");
        }
    }
}
