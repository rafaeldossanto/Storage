using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Catalog;
using Storage.Domain.Stock;

namespace Storage.Application.Stock;

public sealed record StockItemDto(
    Guid ProductId,
    string Name,
    Guid CategoryId,
    bool Active,
    int MinimumStock,
    int Quantity,
    long StockValueCents,
    long AverageCostCents,
    DateOnly? NextExpiry,
    bool BelowMinimum);

public sealed record BatchDto(
    Guid Id,
    int InitialQuantity,
    int RemainingQuantity,
    long UnitCostCents,
    DateOnly? ExpiryDate,
    DateTimeOffset ReceivedAt,
    BatchStatus Status,
    Guid? ReceiptId);

public sealed record MovementDto(
    Guid Id,
    MovementType Type,
    int Quantity,
    long UnitCostCents,
    long ValueCents,
    DateTimeOffset OccurredAt,
    Guid BatchId,
    Guid? UserId,
    Guid? DocumentId,
    string? Note);

public sealed record ProductStockDto(StockItemDto Stock, IReadOnlyList<BatchDto> Batches, IReadOnlyList<MovementDto> Movements);

public sealed record StockSummaryDto(int ProductsInStock, int Units, long StockValueCents, int BelowMinimumCount);

/// <summary>What is on the shelf: read-only views over the ledger.</summary>
public sealed class StockQueries(IStockStore stock, IProductRepository products, ICategoryRepository categories)
{
    public const int MovementHistoryLimit = 50;

    /// <summary>Every product of a category, optionally with its whole branch, and its stock.</summary>
    public async Task<IReadOnlyList<StockItemDto>> ListByCategoryAsync(
        Guid categoryId,
        bool includeDescendants,
        CancellationToken cancellationToken = default)
    {
        var category = await categories.FindAsync(categoryId, cancellationToken)
            ?? throw UseCaseException.NotFound(ErrorCodes.CategoryNotFound, $"Category {categoryId} does not exist.");

        var listed = await products.ListByCategoryAsync(category, includeDescendants, cancellationToken);
        return await WithLevelsAsync(listed, cancellationToken);
    }

    /// <summary>
    /// What is running out: active products below the minimum the shop set for them, the
    /// emptiest first. The list a shopkeeper takes to the supplier.
    /// </summary>
    public async Task<IReadOnlyList<StockItemDto>> ListBelowMinimumAsync(CancellationToken cancellationToken = default)
    {
        var tracked = (await products.ListWithMinimumStockAsync(cancellationToken))
            .Where(product => product.Active)
            .ToArray();

        return (await WithLevelsAsync(tracked, cancellationToken))
            .Where(item => item.BelowMinimum)
            .OrderBy(item => (double)item.Quantity / item.MinimumStock)
            .ThenBy(item => item.Name, StringComparer.InvariantCultureIgnoreCase)
            .ToArray();
    }

    public async Task<ProductStockDto> GetProductAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var product = await products.FindAsync(productId, cancellationToken)
            ?? throw UseCaseException.NotFound(ErrorCodes.ProductNotFound, $"Product {productId} does not exist.");

        var batches = await stock.ListBatchesAsync(productId, availableOnly: true, cancellationToken);
        var movements = await stock.ListMovementsAsync(productId, MovementHistoryLimit, cancellationToken);
        var valuation = StockValuation.Of(batches);

        var item = ToItem(product, StockService.ToDto(productId, valuation, batches));

        return new ProductStockDto(
            item,
            Fefo.Order(batches).Select(ToDto).ToArray(),
            movements.Select(ToDto).ToArray());
    }

    public async Task<StockSummaryDto> SummaryAsync(CancellationToken cancellationToken = default)
    {
        var levels = await stock.AllLevelsAsync(cancellationToken);
        var belowMinimum = await ListBelowMinimumAsync(cancellationToken);

        return new StockSummaryDto(
            levels.Count(level => level.Quantity > 0),
            levels.Sum(level => level.Quantity),
            levels.Sum(level => level.Value.Cents),
            belowMinimum.Count);
    }

    private async Task<IReadOnlyList<StockItemDto>> WithLevelsAsync(
        IReadOnlyList<Product> listed,
        CancellationToken cancellationToken)
    {
        var levels = await stock.LevelsAsync(listed.Select(product => product.Id).ToArray(), cancellationToken);

        return listed
            .Select(product => ToItem(product, StockService.ToDto(levels[product.Id])))
            .ToArray();
    }

    private static StockItemDto ToItem(Product product, StockLevelDto level) => new(
        product.Id,
        product.Name,
        product.CategoryId,
        product.Active,
        product.MinimumStock,
        level.Quantity,
        level.StockValueCents,
        level.AverageCostCents,
        level.NextExpiry,
        // A minimum of zero means the shop does not watch this product.
        BelowMinimum: product.MinimumStock > 0 && level.Quantity < product.MinimumStock);

    private static BatchDto ToDto(Batch batch) => new(
        batch.Id,
        batch.InitialQuantity,
        batch.RemainingQuantity,
        batch.UnitCost.Cents,
        batch.ExpiryDate,
        batch.ReceivedAt,
        batch.Status,
        batch.ReceiptId);

    private static MovementDto ToDto(StockMovement movement) => new(
        movement.Id,
        movement.Type,
        movement.Quantity,
        movement.UnitCost.Cents,
        movement.Value.Cents,
        movement.OccurredAt,
        movement.BatchId,
        movement.UserId,
        movement.DocumentId,
        movement.Note);
}
