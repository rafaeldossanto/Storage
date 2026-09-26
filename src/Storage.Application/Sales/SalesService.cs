using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Application.Stock;
using Storage.Domain.Catalog;
using Storage.Domain.Common;
using Storage.Domain.Pricing;
using Storage.Domain.Sales;
using Storage.Domain.Stock;

namespace Storage.Application.Sales;

/// <summary>A product and how many base units of it: a twelve-pack scanned twice is 24.</summary>
public sealed record SaleItemRequest(Guid ProductId, int Quantity);

public sealed record RegisterSaleRequest(IReadOnlyList<SaleItemRequest> Items);

/// <remarks>No cost here: the till sees what was charged; what it cost is in the locked report.</remarks>
public sealed record SaleLineDto(Guid ProductId, string ProductName, int Quantity, long UnitPriceCents, long TotalCents);

public sealed record SaleDto(
    Guid Id,
    DateTimeOffset SoldAt,
    SaleStatus Status,
    long TotalCents,
    IReadOnlyList<SaleLineDto> Lines,
    DateTimeOffset CancellableUntil);

/// <summary>
/// Rings up sales: stock leaves the batches that expire first, at the price the discount
/// rules give, and the sale remembers what those very units had cost.
/// </summary>
public sealed class SalesService(
    IStockStore stock,
    ISaleStore sales,
    IProductRepository products,
    ICategoryRepository categories,
    IDiscountRuleRepository discounts,
    IShopCalendar calendar,
    ITenantContext tenant,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    /// <summary>
    /// Records a sale and takes its units off the shelf, all in one commit: either the whole
    /// sale is on the books with its stock gone, or nothing happened.
    /// </summary>
    public async Task<SaleDto> RegisterAsync(RegisterSaleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The same product scanned in several places of the cart is one line.
        var items = (request.Items ?? [])
            .GroupBy(item => item.ProductId)
            .Select(group => new SaleItemRequest(group.Key, group.Sum(item => item.Quantity)))
            .ToArray();

        var now = clock.GetUtcNow();
        var today = await calendar.TodayAsync(cancellationToken);
        var rules = await discounts.ListActiveAsync(cancellationToken);

        var sale = Sale.Start(tenant.TenantId, currentUser.UserId, now);
        var changes = new StockChanges();

        for (var index = 0; index < items.Length; index++)
        {
            try
            {
                await AddLineAsync(sale, changes, items[index], rules, today, now, cancellationToken);
            }
            catch (Exception refusal) when (refusal is DomainException or UseCaseException)
            {
                // Which product of the cart was refused, 1-based, so the till can point at it.
                refusal.Data[ReceivingService.LineKey] = index + 1;
                throw;
            }
        }

        sale.RequireLines();
        changes.Attach(sale);

        await stock.CommitAsync(changes, cancellationToken);
        return ToDto(sale);
    }

    /// <summary>
    /// Undoes a sale made moments ago - the wrong product scanned - putting its units back
    /// into the very batches they came from.
    /// </summary>
    public async Task<SaleDto> CancelAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        var sale = await sales.FindAsync(saleId, cancellationToken)
            ?? throw UseCaseException.NotFound(ErrorCodes.SaleNotFound, $"Sale {saleId} does not exist.");

        var readVersion = sale.Version;
        var now = clock.GetUtcNow();
        sale.Cancel(currentUser.UserId, now);

        var taken = (await stock.ListMovementsOfDocumentAsync(sale.Id, cancellationToken))
            .Where(movement => movement.Type == MovementType.Sale)
            .ToArray();

        var batches = (await stock.ListBatchesByIdsAsync(taken.Select(movement => movement.BatchId).ToArray(), cancellationToken))
            .ToDictionary(batch => batch.Id);

        var changes = new StockChanges();

        foreach (var movement in taken)
        {
            var batch = batches[movement.BatchId];
            var quantity = -movement.Quantity;

            changes.Change(batch, returned => returned.Return(quantity));
            changes.Record(StockMovement.SaleReturn(batch, quantity, now, currentUser.UserId, sale.Id));
        }

        // Written only if nobody cancelled it in the meantime; otherwise the whole commit -
        // the units put back included - is refused.
        changes.Update(sale, readVersion);

        await stock.CommitAsync(changes, cancellationToken);
        return ToDto(sale);
    }

    private async Task AddLineAsync(
        Sale sale,
        StockChanges changes,
        SaleItemRequest item,
        IReadOnlyList<DiscountRule> rules,
        DateOnly today,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (item.Quantity is < 1 or > Sale.MaxQuantityPerLine)
        {
            throw new DomainException(
                DomainErrors.SaleQuantityInvalid, $"A line sells from 1 to {Sale.MaxQuantityPerLine} units.");
        }

        var product = await products.FindAsync(item.ProductId, cancellationToken)
            ?? throw UseCaseException.NotFound(ErrorCodes.ProductNotFound, $"Product {item.ProductId} does not exist.");

        var category = await categories.FindAsync(product.CategoryId, cancellationToken)
            ?? throw new InvalidOperationException($"Product {product.Id} points at a category that does not exist.");

        var batches = await stock.ListBatchesAsync(product.Id, availableOnly: true, cancellationToken);
        var plan = Fefo.Allocate(batches, item.Quantity);

        // Priced on the batch that leaves first: a "expiring within 3 days" discount applies
        // when that is the batch the customer takes.
        var price = DiscountEngine.Quote(product, category, rules, today, plan[0].Batch.ExpiryDate).FinalPrice;

        sale.AddLine(product, price, plan);

        foreach (var (batch, quantity) in plan)
        {
            changes.Change(batch, sold => sold.Take(quantity));
            changes.Record(StockMovement.Outflow(MovementType.Sale, batch, quantity, now, currentUser.UserId, sale.Id));
        }
    }

    private static SaleDto ToDto(Sale sale) => new(
        sale.Id,
        sale.SoldAt,
        sale.Status,
        sale.Total.Cents,
        sale.Lines
            .Select(line => new SaleLineDto(line.ProductId, line.ProductName, line.Quantity, line.UnitPrice.Cents, line.Revenue.Cents))
            .ToArray(),
        sale.SoldAt + Sale.CancellationWindow);
}
