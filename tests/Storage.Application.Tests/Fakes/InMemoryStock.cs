using Storage.Application.Abstractions;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Tests.Fakes;

internal sealed class FixedUser(Guid userId) : ICurrentUser
{
    public Guid UserId { get; } = userId;
}

internal sealed class FixedCalendar(DateOnly today) : IShopCalendar
{
    public DateOnly Today { get; set; } = today;

    public Task<DateOnly> TodayAsync(CancellationToken cancellationToken = default) => Task.FromResult(Today);
}

/// <summary>
/// In-memory ledger. Batches are held by reference, so a committed change is already in
/// place; what the fake adds is a record of every commit, and a switch to make the next one
/// fail the way a concurrent change would.
/// </summary>
internal sealed class InMemoryStockStore(Guid tenantId) : IStockStore
{
    public List<Batch> Batches { get; } = [];
    public List<StockMovement> Movements { get; } = [];
    public List<object> Documents { get; } = [];
    public int Commits { get; private set; }

    public Task<IReadOnlyList<Batch>> ListBatchesAsync(
        Guid productId,
        bool availableOnly,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Batch>>(Batches
            .Where(batch => batch.TenantId == tenantId && batch.ProductId == productId)
            .Where(batch => !availableOnly || batch.Status == BatchStatus.Available)
            .ToArray());

    public Task<IReadOnlyList<Batch>> ListExpiredBatchesAsync(DateOnly shopDate, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Batch>>(Batches
            .Where(batch => batch.TenantId == tenantId && batch.Status == BatchStatus.Available && batch.HasExpiredOn(shopDate))
            .ToArray());

    public Task<IReadOnlyList<StockMovement>> ListMovementsAsync(Guid productId, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StockMovement>>(Movements
            .Where(movement => movement.TenantId == tenantId && movement.ProductId == productId)
            .OrderByDescending(movement => movement.OccurredAt)
            .Take(limit)
            .ToArray());

    public Task<IReadOnlyDictionary<Guid, StockLevel>> LevelsAsync(
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<Guid, StockLevel> levels = productIds.ToDictionary(id => id, id =>
        {
            var available = Batches.Where(batch => batch.ProductId == id && batch.IsAvailable).ToArray();
            var valuation = StockValuation.Of(available);
            return new StockLevel(id, valuation.Quantity, valuation.Value, available.Min(batch => batch.ExpiryDate));
        });

        return Task.FromResult(levels);
    }

    public Task CommitAsync(StockChanges changes, CancellationToken cancellationToken = default)
    {
        Commits++;
        Batches.AddRange(changes.NewBatches);
        Movements.AddRange(changes.Movements);
        Documents.AddRange(changes.Documents);
        return Task.CompletedTask;
    }

    public Batch Seed(Guid productId, int quantity, long unitCostCents, DateOnly? expiry, DateTimeOffset at)
    {
        var batch = Batch.Receive(tenantId, productId, quantity, Money.FromCents(unitCostCents), expiry, at);
        Batches.Add(batch);
        return batch;
    }
}
