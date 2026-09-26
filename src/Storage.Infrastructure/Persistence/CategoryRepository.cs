using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Catalog;

namespace Storage.Infrastructure.Persistence;

public sealed class CategoryRepository(MongoStorageContext context, ITenantContext tenant)
    : ICategoryRepository
{
    private const string NameTaken = "A sibling category already has this name.";

    private FilterDefinition<Category> OfThisShop =>
        Builders<Category>.Filter.Eq(category => category.TenantId, tenant.TenantId);

    public async Task<Category?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var filter = Builders<Category>.Filter.And(
            OfThisShop,
            Builders<Category>.Filter.Eq(category => category.Id, id));

        return await context.Categories.Find(filter).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken = default)
    {
        // Sorting by path yields a depth-first order, so the list is already a tree walk:
        // a parent is always immediately followed by its own branch.
        return await context.Categories
            .Find(OfThisShop)
            .SortBy(category => category.Path)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Category>> ListDescendantsAsync(
        Category root,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);

        // Anchored at the start so the index is used, and escaped so a path can never be
        // read as a pattern.
        var prefix = new BsonRegularExpression($"^{Regex.Escape(root.DescendantPathPrefix)}");

        var filter = Builders<Category>.Filter.And(
            OfThisShop,
            Builders<Category>.Filter.Regex(category => category.Path, prefix),
            Builders<Category>.Filter.Ne(category => category.Id, root.Id));

        return await context.Categories
            .Find(filter)
            .SortBy(category => category.Path)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Category category, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(category);
        Guard(category);

        await DuplicateKey.GuardAsync(
            () => context.Categories.InsertOneAsync(category, options: null, cancellationToken),
            ErrorCodes.CategoryNameTaken,
            NameTaken);
    }

    public async Task UpdateAsync(Category category, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(category);
        Guard(category);

        var filter = Builders<Category>.Filter.And(
            OfThisShop,
            Builders<Category>.Filter.Eq(stored => stored.Id, category.Id));

        await DuplicateKey.GuardAsync(
            () => context.Categories.ReplaceOneAsync(filter, category, new ReplaceOptions(), cancellationToken),
            ErrorCodes.CategoryNameTaken,
            NameTaken);
    }

    public async Task UpdateManyAsync(
        IReadOnlyCollection<Category> categories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categories);

        if (categories.Count == 0)
        {
            return;
        }

        var writes = new List<WriteModel<Category>>(categories.Count);

        foreach (var category in categories)
        {
            Guard(category);

            var filter = Builders<Category>.Filter.And(
                OfThisShop,
                Builders<Category>.Filter.Eq(stored => stored.Id, category.Id));

            writes.Add(new ReplaceOneModel<Category>(filter, category));
        }

        // A moved branch is rewritten in one round trip: leaving half the descendants with
        // the old path would strand them outside every prefix query.
        await DuplicateKey.GuardAsync(
            () => context.Categories.BulkWriteAsync(writes, new BulkWriteOptions(), cancellationToken),
            ErrorCodes.CategoryNameTaken,
            NameTaken);
    }

    public async Task<bool> HasProductsAsync(
        Guid categoryId,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<Product>.Filter.And(
            Builders<Product>.Filter.Eq(product => product.TenantId, tenant.TenantId),
            Builders<Product>.Filter.Eq(product => product.CategoryId, categoryId));

        return await context.Products.Find(filter).AnyAsync(cancellationToken);
    }

    /// <summary>
    /// Refuses to write a record belonging to another shop, even if a caller hands one over.
    /// </summary>
    private void Guard(Category category)
    {
        if (category.TenantId != tenant.TenantId)
        {
            throw new InvalidOperationException(
                "Attempted to write a category that belongs to another tenant.");
        }
    }
}
