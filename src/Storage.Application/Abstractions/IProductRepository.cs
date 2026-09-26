using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Abstractions;

public interface IProductRepository
{
    Task<Product?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Products with a minimum stock set - the only ones that can fall below it.</summary>
    Task<IReadOnlyList<Product>> ListWithMinimumStockAsync(CancellationToken cancellationToken = default);

    /// <summary>Several products in one round trip - for screens that list many at once.</summary>
    Task<IReadOnlyList<Product>> ListByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The product a scanned barcode belongs to, matched against every packaging - this is
    /// the lookup the whole product hangs on.
    /// </summary>
    Task<Product?> FindByGtinAsync(Gtin gtin, CancellationToken cancellationToken = default);

    /// <summary>Free-text search over the name, for goods that carry no barcode.</summary>
    Task<IReadOnlyList<Product>> SearchAsync(
        string term,
        int limit = 20,
        CancellationToken cancellationToken = default);

    /// <summary>Products of one category, optionally including its whole branch.</summary>
    Task<IReadOnlyList<Product>> ListByCategoryAsync(
        Category category,
        bool includeDescendants,
        CancellationToken cancellationToken = default);

    Task AddAsync(Product product, CancellationToken cancellationToken = default);

    Task UpdateAsync(Product product, CancellationToken cancellationToken = default);
}
