using Storage.Domain.Stock;

namespace Storage.Application.Abstractions;

/// <summary>The stock counts of the current shop.</summary>
public interface ICountStore
{
    Task<StockCount?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<StockCount?> FindOpenAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockCount>> ListRecentAsync(int limit, CancellationToken cancellationToken = default);

    Task AddAsync(StockCount count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets how many of a product were counted, as one atomic update of that item alone -
    /// two people counting different products at once never overwrite each other. Returns
    /// false when the count is not open.
    /// </summary>
    Task<bool> SetCountedAsync(Guid countId, Guid productId, int quantity, CancellationToken cancellationToken = default);

    /// <summary>Adds scanned units to a product's count, atomically. False when the count is not open.</summary>
    Task<bool> AddCountedAsync(Guid countId, Guid productId, int units, CancellationToken cancellationToken = default);
}
