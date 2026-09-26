using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Tests.Fakes;

internal sealed class FixedTenant(Guid tenantId) : ITenantContext
{
    public Guid TenantId { get; } = tenantId;
}

/// <summary>
/// Stands in for the MongoDB repository so the use cases can be tested on their own. It
/// mirrors the two behaviours the use cases depend on: every read is scoped to the current
/// shop, and the unique sibling-name index turns a clash into a conflict.
/// </summary>
internal sealed class InMemoryCategoryRepository(ITenantContext tenant) : ICategoryRepository
{
    private readonly List<Category> _stored = [];

    public IReadOnlyList<Guid> LastBulkUpdate { get; private set; } = [];

    public IReadOnlyList<Category> Stored => _stored;

    public Task<Category?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(OfThisShop().FirstOrDefault(category => category.Id == id));

    public Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Category>>(
            OfThisShop().OrderBy(category => category.Path, StringComparer.Ordinal).ToArray());

    public Task<IReadOnlyList<Category>> ListDescendantsAsync(
        Category root,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Category>>(OfThisShop()
            .Where(category => category.Id != root.Id
                && category.Path.StartsWith(root.DescendantPathPrefix, StringComparison.Ordinal))
            .ToArray());

    public Task AddAsync(Category category, CancellationToken cancellationToken = default)
    {
        var siblingNameTaken = OfThisShop().Any(existing =>
            existing.ParentId == category.ParentId && existing.Name == category.Name);

        if (siblingNameTaken)
        {
            throw UseCaseException.Conflict(ErrorCodes.CategoryNameTaken, "Sibling name taken.");
        }

        _stored.Add(category);
        return Task.CompletedTask;
    }

    // Entities are held by reference, so an update has nothing left to write.
    public Task UpdateAsync(Category category, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UpdateManyAsync(
        IReadOnlyCollection<Category> categories,
        CancellationToken cancellationToken = default)
    {
        LastBulkUpdate = categories.Select(category => category.Id).ToArray();
        return Task.CompletedTask;
    }

    public Task<bool> HasProductsAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public void Seed(Category category) => _stored.Add(category);

    private IEnumerable<Category> OfThisShop() =>
        _stored.Where(category => category.TenantId == tenant.TenantId);
}

/// <param name="categories">
/// Given, listing a category with its descendants walks the tree the way the real
/// repository does; without it only the category's own products come back.
/// </param>
internal sealed class InMemoryProductRepository(ITenantContext tenant, InMemoryCategoryRepository? categories = null)
    : IProductRepository
{
    private readonly List<Product> _stored = [];

    public IReadOnlyList<Product> Stored => _stored;

    public Task<Product?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(OfThisShop().FirstOrDefault(product => product.Id == id));

    public Task<IReadOnlyList<Product>> ListWithMinimumStockAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Product>>(OfThisShop().Where(product => product.MinimumStock > 0).ToArray());

    public Task<IReadOnlyList<Product>> ListByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Product>>(OfThisShop().Where(product => ids.Contains(product.Id)).ToArray());

    public Task<Product?> FindByGtinAsync(Gtin gtin, CancellationToken cancellationToken = default) =>
        Task.FromResult(OfThisShop().FirstOrDefault(product => product.FindPackaging(gtin) is not null));

    public Task<Paged<Product>> SearchAsync(
        string term,
        PageRequest page,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Paged<Product>.Slice(
            Alphabetical(OfThisShop().Where(product => product.Name.Contains(term, StringComparison.OrdinalIgnoreCase))),
            page));

    public Task<Paged<Product>> ListByCategoryAsync(
        Category category,
        bool includeDescendants,
        PageRequest page,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Paged<Product>.Slice(
            Alphabetical(OfThisShop().Where(product => InBranch(product.CategoryId, category, includeDescendants))),
            page));

    // Stands in for the database's Portuguese collation.
    private static Product[] Alphabetical(IEnumerable<Product> products) =>
        products
            .OrderBy(product => product.Name, StringComparer.Create(new System.Globalization.CultureInfo("pt-BR"), ignoreCase: true))
            .ThenBy(product => product.Id)
            .ToArray();

    private bool InBranch(Guid productCategory, Category category, bool includeDescendants) =>
        productCategory == category.Id
        || (includeDescendants
            && categories?.Stored.Any(stored =>
                stored.Id == productCategory
                && stored.Path.StartsWith(category.DescendantPathPrefix, StringComparison.Ordinal)) == true);

    public Task AddAsync(Product product, CancellationToken cancellationToken = default)
    {
        _stored.Add(product);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Product product, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    private IEnumerable<Product> OfThisShop() =>
        _stored.Where(product => product.TenantId == tenant.TenantId);
}
