using Storage.Domain.Catalog;

namespace Storage.Application.Abstractions;

public interface ICategoryRepository
{
    Task<Category?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Every category of the current shop, ordered for display as a tree.</summary>
    Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The whole branch below <paramref name="root"/>, matched by path prefix - the query
    /// a discount rule needs to know which products it reaches.
    /// </summary>
    Task<IReadOnlyList<Category>> ListDescendantsAsync(
        Category root,
        CancellationToken cancellationToken = default);

    Task AddAsync(Category category, CancellationToken cancellationToken = default);

    Task UpdateAsync(Category category, CancellationToken cancellationToken = default);

    /// <summary>Persists a whole moved branch in one round trip.</summary>
    Task UpdateManyAsync(
        IReadOnlyCollection<Category> categories,
        CancellationToken cancellationToken = default);

    Task<bool> HasProductsAsync(Guid categoryId, CancellationToken cancellationToken = default);
}
