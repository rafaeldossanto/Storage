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

    /// <summary>
    /// Free-text search over the name, for goods that carry no barcode. Alphabetical, the way
    /// a person reads Portuguese: "Água" among the A's, not after "Zebra".
    /// </summary>
    Task<Paged<Product>> SearchAsync(
        string term,
        PageRequest page,
        CancellationToken cancellationToken = default);

    /// <summary>Products of one category, optionally including its whole branch, alphabetical.</summary>
    Task<Paged<Product>> ListByCategoryAsync(
        Category category,
        bool includeDescendants,
        PageRequest page,
        CancellationToken cancellationToken = default);

    Task AddAsync(Product product, CancellationToken cancellationToken = default);

    Task UpdateAsync(Product product, CancellationToken cancellationToken = default);

    /// <summary>Removes the product for good. The caller checks nothing refers to it.</summary>
    Task DeleteAsync(Product product, CancellationToken cancellationToken = default);
}
