using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Stock;

namespace Storage.Application.Stock;

public enum StockRemovalReason
{
    /// <summary>Broken, spoiled before its date, stolen - a loss.</summary>
    Damage,

    /// <summary>Sent back; the supplier usually credits it, so it is not a loss.</summary>
    ReturnToSupplier,
}

public sealed record RemoveStockRequest(int Quantity, StockRemovalReason Reason, string? Note = null);

public sealed record StockLevelDto(
    Guid ProductId,
    int Quantity,
    long StockValueCents,
    long AverageCostCents,
    DateOnly? NextExpiry);

public sealed class StockService(
    IStockStore stock,
    IProductRepository products,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public const int NoteMaxLength = 200;

    /// <summary>
    /// Takes units off the shelf for a reason other than expiry, from the batches that expire
    /// soonest - what goes out first is what would have spoiled first anyway.
    /// </summary>
    public async Task<StockLevelDto> RemoveAsync(
        Guid productId,
        RemoveStockRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Quantity < 1)
        {
            throw UseCaseException.Invalid(ErrorCodes.StockQuantityInvalid, "Remove at least one unit.");
        }

        var note = NormalizeNote(request.Note);

        _ = await products.FindAsync(productId, cancellationToken)
            ?? throw UseCaseException.NotFound(ErrorCodes.ProductNotFound, $"Product {productId} does not exist.");

        var batches = await stock.ListBatchesAsync(productId, availableOnly: true, cancellationToken);
        var plan = Fefo.Allocate(batches, request.Quantity);

        var type = request.Reason == StockRemovalReason.ReturnToSupplier
            ? MovementType.ReturnToSupplier
            : MovementType.DamageLoss;

        var now = clock.GetUtcNow();
        var changes = new StockChanges();

        foreach (var (batch, quantity) in plan)
        {
            changes.Change(batch, taken => taken.Take(quantity));
            changes.Record(StockMovement.Outflow(type, batch, quantity, now, currentUser.UserId, note: note));
        }

        await stock.CommitAsync(changes, cancellationToken);

        return ToDto(productId, StockValuation.Of(batches), batches);
    }

    internal static StockLevelDto ToDto(Guid productId, StockValuation valuation, IEnumerable<Batch> batches) => new(
        productId,
        valuation.Quantity,
        valuation.Value.Cents,
        valuation.AverageCost.Cents,
        batches.Where(batch => batch.IsAvailable).Min(batch => batch.ExpiryDate));

    internal static StockLevelDto ToDto(StockLevel level) => new(
        level.ProductId,
        level.Quantity,
        level.Value.Cents,
        level.AverageCost.Cents,
        level.NextExpiry);

    private static string? NormalizeNote(string? note)
    {
        var trimmed = note?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= NoteMaxLength
            ? trimmed
            : throw UseCaseException.Invalid(
                ErrorCodes.StockNoteTooLong, $"A note is limited to {NoteMaxLength} characters.");
    }
}
