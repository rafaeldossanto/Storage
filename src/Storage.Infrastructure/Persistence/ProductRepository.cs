using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;

namespace Storage.Infrastructure.Persistence;

public sealed class ProductRepository(
    MongoStorageContext context,
    ITenantContext tenant,
    TimeProvider clock) : IProductRepository
{
    private FilterDefinition<Product> OfThisShop =>
        Builders<Product>.Filter.Eq(product => product.TenantId, tenant.TenantId);

    public async Task<Product?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var filter = Builders<Product>.Filter.And(
            OfThisShop,
            Builders<Product>.Filter.Eq(product => product.Id, id));

        return await context.Products.Find(filter).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Product?> FindByGtinAsync(
        Gtin gtin,
        CancellationToken cancellationToken = default)
    {
        // Matches the normalised fourteen digit form inside the embedded packagings, so any
        // printed length - EAN-8, EAN-13, the DUN-14 on the outer case - finds the product.
        var filter = Builders<Product>.Filter.And(
            OfThisShop,
            Builders<Product>.Filter.Eq(MongoStorageContext.PackagingGtinField, gtin.Value));

        return await context.Products.Find(filter).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Product>> SearchAsync(
        string term,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        // Case-insensitive contains: good enough for a few thousand products, and the
        // escape keeps a customer's search text from being read as a pattern. A text index
        // is the upgrade path if a shop's catalogue outgrows it.
        var pattern = new BsonRegularExpression(Regex.Escape(term.Trim()), "i");

        var filter = Builders<Product>.Filter.And(
            OfThisShop,
            Builders<Product>.Filter.Regex(product => product.Name, pattern));

        return await context.Products
            .Find(filter)
            .SortBy(product => product.Name)
            .Limit(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Product>> ListByCategoryAsync(
        Category category,
        bool includeDescendants,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(category);

        FilterDefinition<Product> categoryFilter;

        if (includeDescendants)
        {
            var prefix = new BsonRegularExpression(
                $"^{Regex.Escape(category.DescendantPathPrefix)}");

            var branch = await context.Categories
                .Find(Builders<Category>.Filter.And(
                    Builders<Category>.Filter.Eq(stored => stored.TenantId, tenant.TenantId),
                    Builders<Category>.Filter.Regex(stored => stored.Path, prefix)))
                .Project(stored => stored.Id)
                .ToListAsync(cancellationToken);

            categoryFilter = Builders<Product>.Filter.In(product => product.CategoryId, branch);
        }
        else
        {
            categoryFilter = Builders<Product>.Filter.Eq(product => product.CategoryId, category.Id);
        }

        return await context.Products
            .Find(Builders<Product>.Filter.And(OfThisShop, categoryFilter))
            .SortBy(product => product.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Product product, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        Guard(product);

        product.MarkCreated(clock.GetUtcNow());

        await context.Products.InsertOneAsync(product, options: null, cancellationToken);
    }

    public async Task UpdateAsync(Product product, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        Guard(product);

        product.MarkUpdated(clock.GetUtcNow());

        var filter = Builders<Product>.Filter.And(
            OfThisShop,
            Builders<Product>.Filter.Eq(stored => stored.Id, product.Id));

        await context.Products.ReplaceOneAsync(filter, product, new ReplaceOptions(), cancellationToken);
    }

    /// <summary>
    /// Refuses to write a record belonging to another shop, even if a caller hands one over.
    /// </summary>
    private void Guard(Product product)
    {
        if (product.TenantId != tenant.TenantId)
        {
            throw new InvalidOperationException(
                "Attempted to write a product that belongs to another tenant.");
        }
    }
}
