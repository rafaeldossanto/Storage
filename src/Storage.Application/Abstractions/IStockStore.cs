using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Abstractions;

/// <summary>What one product holds right now, derived from its available batches.</summary>
public sealed record StockLevel(Guid ProductId, int Quantity, Money Value, DateOnly? NextExpiry)
{
    public Money AverageCost => new StockValuation(Quantity, Value).AverageCost;

    public static StockLevel Empty(Guid productId) => new(productId, 0, Money.Zero, null);
}

/// <summary>
/// The stock ledger of the current shop: batches and movements.
/// </summary>
public interface IStockStore
{
    Task<IReadOnlyList<Batch>> ListBatchesAsync(
        Guid productId,
        bool availableOnly,
        CancellationToken cancellationToken = default);

    /// <summary>Available batches of the shop that have passed <paramref name="shopDate"/>.</summary>
    Task<IReadOnlyList<Batch>> ListExpiredBatchesAsync(DateOnly shopDate, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockMovement>> ListMovementsAsync(
        Guid productId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Balance, stock value and nearest expiry per product, summed in the database.</summary>
    Task<IReadOnlyDictionary<Guid, StockLevel>> LevelsAsync(
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a set of changes all at once, or none of them.
    /// </summary>
    /// <remarks>
    /// The only way stock is written. A batch that changed since it was read makes the whole
    /// commit fail with a conflict instead of overwriting what the other change did.
    /// </remarks>
    Task CommitAsync(StockChanges changes, CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything one stock operation changes, gathered in memory and committed atomically.
/// </summary>
/// <remarks>
/// A batch has to be changed through <see cref="Change"/>, which remembers how it looked
/// before. The store writes the change only if the batch still looks like that - an
/// optimistic check that turns a concurrent edit into a conflict rather than a lost update.
/// </remarks>
public sealed class StockChanges
{
    private readonly List<Batch> _newBatches = [];
    private readonly Dictionary<Guid, ChangedBatch> _changedBatches = [];
    private readonly List<StockMovement> _movements = [];
    private readonly List<object> _documents = [];

    public IReadOnlyList<Batch> NewBatches => _newBatches;

    public IReadOnlyCollection<ChangedBatch> ChangedBatches => _changedBatches.Values;

    public IReadOnlyList<StockMovement> Movements => _movements;

    /// <summary>Documents written alongside - a goods receipt, a count.</summary>
    public IReadOnlyList<object> Documents => _documents;

    public bool IsEmpty =>
        _newBatches.Count == 0 && _changedBatches.Count == 0 && _movements.Count == 0 && _documents.Count == 0;

    public void Add(Batch batch) => _newBatches.Add(batch);

    public void Record(StockMovement movement) => _movements.Add(movement);

    public void Attach(object document) => _documents.Add(document);

    /// <summary>Changes an existing batch, remembering what it looked like first.</summary>
    public T Change<T>(Batch batch, Func<Batch, T> change)
    {
        Remember(batch);
        return change(batch);
    }

    public void Change(Batch batch, Action<Batch> change)
    {
        Remember(batch);
        change(batch);
    }

    private void Remember(Batch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        // Only the first look counts: that is the state the database still has.
        _changedBatches.TryAdd(batch.Id, new ChangedBatch(batch, batch.RemainingQuantity, batch.Status));
    }

    public sealed record ChangedBatch(Batch Batch, int ExpectedRemaining, BatchStatus ExpectedStatus);
}
