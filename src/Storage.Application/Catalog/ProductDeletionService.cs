using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Pricing;

namespace Storage.Application.Catalog;

/// <summary>
/// Deletes a product registered by mistake - the "undo" a screen offers right after
/// registering one, which also frees its barcode to be registered again.
/// </summary>
/// <remarks>
/// Only a product nothing refers to: no stock movement, no discount rule aimed at it, no
/// count that counted it. Anything with a history is deactivated instead, so the ledger
/// never points at a product that is gone. The check and the delete are two steps; a
/// delivery landing in between is possible in principle, but the undo happens seconds
/// after registering, before anyone could have received the product.
/// </remarks>
public sealed class ProductDeletionService(
    IProductRepository products,
    IStockStore stock,
    IDiscountRuleRepository discounts,
    ICountStore counts)
{
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var product = await products.FindAsync(id, cancellationToken)
            ?? throw UseCaseException.NotFound(ErrorCodes.ProductNotFound, $"Product {id} does not exist.");

        if (await InUseAsync(product.Id, cancellationToken))
        {
            throw UseCaseException.Conflict(
                ErrorCodes.ProductInUse,
                "This product has a history; deactivate it instead of deleting it.");
        }

        await products.DeleteAsync(product, cancellationToken);
    }

    private async Task<bool> InUseAsync(Guid productId, CancellationToken cancellationToken) =>
        (await stock.ListMovementsAsync(productId, PageRequest.First(1), cancellationToken)).Total > 0
        || (await discounts.ListAsync(cancellationToken))
            .Any(rule => rule.TargetType == DiscountTarget.Product && rule.TargetId == productId)
        || await counts.AnyCountedAsync(productId, cancellationToken);
}
