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

    // UTC midnight: the fake has no time zone of its own.
    public Task<DateTimeOffset> StartOfDayAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
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

    public Task<Paged<StockMovement>> ListMovementsAsync(Guid productId, PageRequest page, CancellationToken cancellationToken = default) =>
        Task.FromResult(Paged<StockMovement>.Slice(
            Movements
                .Where(movement => movement.TenantId == tenantId && movement.ProductId == productId)
                .OrderByDescending(movement => movement.OccurredAt)
                .ThenByDescending(movement => movement.Id)
                .ToArray(),
            page));

    public Task<IReadOnlyList<Batch>> ListExpiringBatchesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Batch>>(Batches
            .Where(batch => batch.TenantId == tenantId && batch.Status == BatchStatus.Available)
            .Where(batch => batch.ExpiryDate is { } expiry && expiry >= from && expiry <= to)
            .ToArray());

    public Task<IReadOnlyList<MovementTotal>> SumMovementsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<MovementType> types,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MovementTotal>>(Movements
            .Where(movement => movement.TenantId == tenantId && types.Contains(movement.Type))
            .Where(movement => movement.OccurredAt >= from && movement.OccurredAt < to)
            .GroupBy(movement => (movement.ProductId, movement.Type))
            .Select(group => new MovementTotal(
                group.Key.ProductId,
                group.Key.Type,
                group.Sum(movement => movement.Quantity),
                group.Aggregate(Money.Zero, (sum, movement) => sum + movement.Value)))
            .ToArray());

    public Task<Paged<GoodsReceipt>> ListReceiptsAsync(PageRequest page, CancellationToken cancellationToken = default) =>
        Task.FromResult(Paged<GoodsReceipt>.Slice(
            Documents.OfType<GoodsReceipt>()
                .Where(receipt => receipt.TenantId == tenantId)
                .OrderByDescending(receipt => receipt.ReceivedAt)
                .ThenByDescending(receipt => receipt.Id)
                .ToArray(),
            page));

    public Task<GoodsReceipt?> FindReceiptAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Documents.OfType<GoodsReceipt>().FirstOrDefault(receipt => receipt.TenantId == tenantId && receipt.Id == id));

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

    public Task<IReadOnlyList<StockLevel>> AllLevelsAsync(CancellationToken cancellationToken = default)
    {
        var productIds = Batches.Where(batch => batch.TenantId == tenantId && batch.IsAvailable).Select(batch => batch.ProductId).Distinct().ToArray();
        return Task.FromResult<IReadOnlyList<StockLevel>>(LevelsAsync(productIds).Result.Values.ToArray());
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

internal sealed class InMemorySupplierRepository(ITenantContext tenant) : ISupplierRepository
{
    private readonly List<Supplier> _stored = [];

    public Task<Supplier?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_stored.FirstOrDefault(supplier => supplier.TenantId == tenant.TenantId && supplier.Id == id));

    public Task<IReadOnlyList<Supplier>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Supplier>>(_stored.Where(supplier => supplier.TenantId == tenant.TenantId).ToArray());

    public Task AddAsync(Supplier supplier, CancellationToken cancellationToken = default)
    {
        _stored.Add(supplier);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Supplier supplier, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class InMemoryDiscountRuleRepository(ITenantContext tenant) : IDiscountRuleRepository
{
    private readonly List<Storage.Domain.Pricing.DiscountRule> _stored = [];

    public Task<Storage.Domain.Pricing.DiscountRule?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_stored.FirstOrDefault(rule => rule.TenantId == tenant.TenantId && rule.Id == id));

    public Task<IReadOnlyList<Storage.Domain.Pricing.DiscountRule>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Storage.Domain.Pricing.DiscountRule>>(_stored.Where(rule => rule.TenantId == tenant.TenantId).ToArray());

    public Task<IReadOnlyList<Storage.Domain.Pricing.DiscountRule>> ListActiveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Storage.Domain.Pricing.DiscountRule>>(_stored.Where(rule => rule.TenantId == tenant.TenantId && rule.Active).ToArray());

    public Task AddAsync(Storage.Domain.Pricing.DiscountRule rule, CancellationToken cancellationToken = default)
    {
        _stored.Add(rule);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Storage.Domain.Pricing.DiscountRule rule, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class InMemoryCountStore(ITenantContext tenant) : ICountStore
{
    public List<StockCount> Stored { get; } = [];

    public Task<StockCount?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Stored.FirstOrDefault(count => count.TenantId == tenant.TenantId && count.Id == id));

    public Task<StockCount?> FindOpenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Stored.FirstOrDefault(count => count.TenantId == tenant.TenantId && count.Status == StockCountStatus.Open));

    public Task<Paged<StockCount>> ListAsync(PageRequest page, CancellationToken cancellationToken = default) =>
        Task.FromResult(Paged<StockCount>.Slice(
            Stored
                .Where(count => count.TenantId == tenant.TenantId)
                .OrderByDescending(count => count.StartedAt)
                .ThenByDescending(count => count.Id)
                .ToArray(),
            page));

    public Task AddAsync(StockCount count, CancellationToken cancellationToken = default)
    {
        Stored.Add(count);
        return Task.CompletedTask;
    }

    public Task<bool> SetCountedAsync(Guid countId, Guid productId, int quantity, CancellationToken cancellationToken = default) =>
        Apply(countId, count => count.SetCounted(productId, quantity));

    public Task<bool> AddCountedAsync(Guid countId, Guid productId, int units, CancellationToken cancellationToken = default) =>
        Apply(countId, count => count.AddCounted(productId, units));

    private Task<bool> Apply(Guid countId, Action<StockCount> change)
    {
        var count = Stored.FirstOrDefault(stored => stored.Id == countId && stored.Status == StockCountStatus.Open);
        if (count is null)
        {
            return Task.FromResult(false);
        }

        change(count);
        return Task.FromResult(true);
    }
}
