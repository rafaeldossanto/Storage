using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;

namespace Storage.Infrastructure.Persistence;

public sealed class ProductRepository(
    MongoStorageContext context,
    ITenantContext tenant,
    TimeProvider clock) : IProductRepository
{
    private const string BarcodeTaken = "One of these barcodes already belongs to another product.";

    // Alphabetical the way a person reads Portuguese. A plain byte-order sort puts every
    // capital and accented initial after "z": "Água mineral" would come after "Suco".
    private static readonly Collation Portuguese = new("pt");

    private FilterDefinition<Product> OfThisShop =>
        Builders<Product>.Filter.Eq(product => product.TenantId, tenant.TenantId);

    public async Task<Product?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var filter = Builders<Product>.Filter.And(
            OfThisShop,
            Builders<Product>.Filter.Eq(product => product.Id, id));

        return await context.Products.Find(filter).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Product>> ListWithMinimumStockAsync(CancellationToken cancellationToken = default) =>
        await context.Products
            .Find(Builders<Product>.Filter.And(OfThisShop, Builders<Product>.Filter.Gt(product => product.MinimumStock, 0)))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Product>> ListByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        return await context.Products
            .Find(Builders<Product>.Filter.And(OfThisShop, Builders<Product>.Filter.In(product => product.Id, ids)))
            .ToListAsync(cancellationToken);
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

    public async Task<Paged<Product>> SearchAsync(
        string term,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (string.IsNullOrWhiteSpace(term))
        {
            return Paged<Product>.Empty(page);
        }

        // Case-insensitive contains: good enough for a few thousand products, and the
        // escape keeps a customer's search text from being read as a pattern. A text index
        // is the upgrade path if a shop's catalogue outgrows it.
        var pattern = new BsonRegularExpression(Regex.Escape(term.Trim()), "i");

        var filter = Builders<Product>.Filter.And(
            OfThisShop,
            Builders<Product>.Filter.Regex(product => product.Name, pattern));

        return await PageAsync(filter, page, cancellationToken);
    }

    public async Task<Paged<Product>> ListByCategoryAsync(
        Category category,
        bool includeDescendants,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
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

        return await PageAsync(Builders<Product>.Filter.And(OfThisShop, categoryFilter), page, cancellationToken);
    }

    public async Task AddAsync(Product product, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        Guard(product);

        product.MarkCreated(clock.GetUtcNow());

        await DuplicateKey.GuardAsync(
            () => context.Products.InsertOneAsync(product, options: null, cancellationToken),
            ErrorCodes.BarcodeTaken,
            BarcodeTaken);
    }

    public async Task UpdateAsync(Product product, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        Guard(product);

        product.MarkUpdated(clock.GetUtcNow());

        var filter = Builders<Product>.Filter.And(
            OfThisShop,
            Builders<Product>.Filter.Eq(stored => stored.Id, product.Id));

        await DuplicateKey.GuardAsync(
            () => context.Products.ReplaceOneAsync(filter, product, new ReplaceOptions(), cancellationToken),
            ErrorCodes.BarcodeTaken,
            BarcodeTaken);
    }

    /// <summary>
    /// Refuses to write a record belonging to another shop, even if a caller hands one over.
    /// </summary>
    private Task<Paged<Product>> PageAsync(
        FilterDefinition<Product> filter,
        PageRequest page,
        CancellationToken cancellationToken) =>
        context.Products.PageAsync(
            filter,
            Builders<Product>.Sort.Ascending(product => product.Name).Ascending(product => product.Id),
            page,
            cancellationToken,
            new FindOptions { Collation = Portuguese });

    private void Guard(Product product)
    {
        if (product.TenantId != tenant.TenantId)
        {
            throw new InvalidOperationException(
                "Attempted to write a product that belongs to another tenant.");
        }
    }
}
