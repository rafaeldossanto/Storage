using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Abstractions;

/// <summary>What one product holds right now, derived from its available batches.</summary>
public sealed record StockLevel(Guid ProductId, int Quantity, Money Value, DateOnly? NextExpiry)
{
    public Money AverageCost => new StockValuation(Quantity, Value).AverageCost;

    public static StockLevel Empty(Guid productId) => new(productId, 0, Money.Zero, null);
}

/// <summary>Signed quantity and value of one product's movements of one type over a period.</summary>
public sealed record MovementTotal(Guid ProductId, MovementType Type, int Quantity, Money Value);

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

    /// <summary>A product's movements, newest first - the whole ledger, a page at a time.</summary>
    Task<Paged<StockMovement>> ListMovementsAsync(
        Guid productId,
        PageRequest page,
        CancellationToken cancellationToken = default);

    /// <summary>Available batches expiring between two dates, inclusive - the expiry dashboard.</summary>
    Task<IReadOnlyList<Batch>> ListExpiringBatchesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Movements of the given types in [<paramref name="from"/>, <paramref name="to"/>),
    /// summed per product and type in the database.
    /// </summary>
    Task<IReadOnlyList<MovementTotal>> SumMovementsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<MovementType> types,
        CancellationToken cancellationToken = default);

    /// <summary>Deliveries, newest first.</summary>
    Task<Paged<GoodsReceipt>> ListReceiptsAsync(PageRequest page, CancellationToken cancellationToken = default);

    Task<GoodsReceipt?> FindReceiptAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Balance, stock value and nearest expiry per product, summed in the database.</summary>
    Task<IReadOnlyDictionary<Guid, StockLevel>> LevelsAsync(
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default);

    /// <summary>Every product of the shop that has stock, with its level - for the shop totals.</summary>
    Task<IReadOnlyList<StockLevel>> AllLevelsAsync(CancellationToken cancellationToken = default);

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
    private readonly List<(object Document, long ExpectedVersion)> _updatedDocuments = [];

    public IReadOnlyList<Batch> NewBatches => _newBatches;

    public IReadOnlyCollection<ChangedBatch> ChangedBatches => _changedBatches.Values;

    public IReadOnlyList<StockMovement> Movements => _movements;

    /// <summary>Documents written alongside - a goods receipt, a count.</summary>
    public IReadOnlyList<object> Documents => _documents;

    /// <summary>
    /// Existing documents rewritten alongside - a count being closed - each only if its
    /// version still matches what was read.
    /// </summary>
    public IReadOnlyList<(object Document, long ExpectedVersion)> UpdatedDocuments => _updatedDocuments;

    public bool IsEmpty =>
        _newBatches.Count == 0 && _changedBatches.Count == 0 && _movements.Count == 0 && _documents.Count == 0
        && _updatedDocuments.Count == 0;

    public void Add(Batch batch) => _newBatches.Add(batch);

    public void Record(StockMovement movement) => _movements.Add(movement);

    public void Attach(object document) => _documents.Add(document);

    public void Update(object document, long expectedVersion) => _updatedDocuments.Add((document, expectedVersion));

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
