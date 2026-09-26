using MongoDB.Bson;
using MongoDB.Driver;
using Storage.Domain.Accounts;
using Storage.Domain.Catalog;
using Storage.Domain.Pricing;
using Storage.Domain.Sales;
using Storage.Domain.Stock;

namespace Storage.Infrastructure.Persistence;

/// <summary>
/// The shop's database and its collections.
/// </summary>
public sealed class MongoStorageContext
{
    public const string CategoriesCollection = "categories";
    public const string ProductsCollection = "products";
    public const string TenantsCollection = "tenants";
    public const string UsersCollection = "users";
    public const string SessionsCollection = "sessions";
    public const string BatchesCollection = "batches";
    public const string StockMovementsCollection = "stockMovements";
    public const string GoodsReceiptsCollection = "goodsReceipts";
    public const string SuppliersCollection = "suppliers";
    public const string DiscountRulesCollection = "discountRules";
    public const string StockCountsCollection = "stockCounts";
    public const string SalesCollection = "sales";

    /// <summary>
    /// Packagings live inside the product document, so the barcode index reaches into the
    /// embedded array by name. The property itself is unmapped - only the backing field is
    /// stored - which is why filters on it are written as field paths rather than lambdas.
    /// </summary>
    public const string PackagingGtinField = "Packagings.Gtin";

    public MongoStorageContext(IMongoClient client, string databaseName)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        StorageBsonSerialization.Register();

        Client = client;
        Database = client.GetDatabase(databaseName);
    }

    public IMongoClient Client { get; }

    public IMongoDatabase Database { get; }

    public IMongoCollection<Category> Categories =>
        Database.GetCollection<Category>(CategoriesCollection);

    public IMongoCollection<Product> Products =>
        Database.GetCollection<Product>(ProductsCollection);

    public IMongoCollection<Tenant> Tenants => Database.GetCollection<Tenant>(TenantsCollection);

    public IMongoCollection<User> Users => Database.GetCollection<User>(UsersCollection);

    public IMongoCollection<Session> Sessions => Database.GetCollection<Session>(SessionsCollection);

    public IMongoCollection<Batch> Batches => Database.GetCollection<Batch>(BatchesCollection);

    public IMongoCollection<StockMovement> StockMovements =>
        Database.GetCollection<StockMovement>(StockMovementsCollection);

    public IMongoCollection<GoodsReceipt> GoodsReceipts =>
        Database.GetCollection<GoodsReceipt>(GoodsReceiptsCollection);

    public IMongoCollection<Supplier> Suppliers => Database.GetCollection<Supplier>(SuppliersCollection);

    public IMongoCollection<DiscountRule> DiscountRules =>
        Database.GetCollection<DiscountRule>(DiscountRulesCollection);

    public IMongoCollection<StockCount> StockCounts => Database.GetCollection<StockCount>(StockCountsCollection);

    public IMongoCollection<Sale> Sales => Database.GetCollection<Sale>(SalesCollection);

    /// <summary>
    /// Creates the indexes the application depends on.
    /// </summary>
    /// <remarks>
    /// This replaces what migrations did for the relational version. Index creation in
    /// MongoDB is idempotent, so running it on every boot costs nothing and means a fresh
    /// deployment - or a newly signed-up shop - is never left without them.
    /// </remarks>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        await Categories.Indexes.CreateManyAsync(
            [
                // Answers "everything under Beverages" as an anchored prefix scan.
                new CreateIndexModel<Category>(
                    Builders<Category>.IndexKeys
                        .Ascending(category => category.TenantId)
                        .Ascending(category => category.Path)),

                // No two sibling categories share a name inside the same shop.
                new CreateIndexModel<Category>(
                    Builders<Category>.IndexKeys
                        .Ascending(category => category.TenantId)
                        .Ascending(category => category.ParentId)
                        .Ascending(category => category.Name),
                    new CreateIndexOptions { Unique = true }),
            ],
            cancellationToken);

        await Products.Indexes.CreateManyAsync(
            [
                // A barcode is unique within a shop, not across the platform: two different
                // shops stock the same can of energy drink, with the same GS1 code.
                new CreateIndexModel<Product>(
                    new BsonDocument { { "TenantId", 1 }, { PackagingGtinField, 1 } },
                    new CreateIndexOptions { Unique = true }),

                new CreateIndexModel<Product>(
                    Builders<Product>.IndexKeys
                        .Ascending(product => product.TenantId)
                        .Ascending(product => product.CategoryId)),

                new CreateIndexModel<Product>(
                    Builders<Product>.IndexKeys
                        .Ascending(product => product.TenantId)
                        .Ascending(product => product.Name)),
            ],
            cancellationToken);

        await Users.Indexes.CreateManyAsync(
            [
                // The e-mail is the login, typed before the shop is known: unique across the
                // whole platform, not per shop. Also settles two sign-ups racing for it.
                new CreateIndexModel<User>(
                    Builders<User>.IndexKeys.Ascending(user => user.Email),
                    new CreateIndexOptions { Unique = true }),

                new CreateIndexModel<User>(
                    Builders<User>.IndexKeys.Ascending(user => user.TenantId)),
            ],
            cancellationToken);

        await Sessions.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<Session>(
                    Builders<Session>.IndexKeys.Ascending(session => session.TokenHash),
                    new CreateIndexOptions { Unique = true }),

                new CreateIndexModel<Session>(
                    Builders<Session>.IndexKeys.Ascending(session => session.UserId)),

                // TTL: MongoDB deletes a session once its expiry passes. Unlike an expired
                // batch - a business fact that becomes a recorded loss - an expired session
                // is plumbing, and keeping it would only grow the collection.
                new CreateIndexModel<Session>(
                    Builders<Session>.IndexKeys.Ascending(session => session.ExpiresAt),
                    new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }),
            ],
            cancellationToken);

        await Batches.Indexes.CreateManyAsync(
            [
                // A product's batches: the balance, FEFO, the stock screen.
                new CreateIndexModel<Batch>(
                    Builders<Batch>.IndexKeys
                        .Ascending(batch => batch.TenantId)
                        .Ascending(batch => batch.ProductId)
                        .Ascending(batch => batch.Status)),

                // What expires when: the daily expiry job and the expiry dashboard.
                new CreateIndexModel<Batch>(
                    Builders<Batch>.IndexKeys
                        .Ascending(batch => batch.TenantId)
                        .Ascending(batch => batch.Status)
                        .Ascending(batch => batch.ExpiryDate)),
            ],
            cancellationToken);

        await StockMovements.Indexes.CreateManyAsync(
            [
                // A product's history, newest first.
                new CreateIndexModel<StockMovement>(
                    Builders<StockMovement>.IndexKeys
                        .Ascending(movement => movement.TenantId)
                        .Ascending(movement => movement.ProductId)
                        .Descending(movement => movement.OccurredAt)),

                // Losses by type and period: the loss report.
                new CreateIndexModel<StockMovement>(
                    Builders<StockMovement>.IndexKeys
                        .Ascending(movement => movement.TenantId)
                        .Ascending(movement => movement.Type)
                        .Ascending(movement => movement.OccurredAt)),
            ],
            cancellationToken);

        // Recent deliveries, newest first.
        await GoodsReceipts.Indexes.CreateOneAsync(
            new CreateIndexModel<GoodsReceipt>(
                Builders<GoodsReceipt>.IndexKeys
                    .Ascending(receipt => receipt.TenantId)
                    .Descending(receipt => receipt.ReceivedAt)),
            cancellationToken: cancellationToken);

        // Sales by period: the sales report sums a day, a month or a year of them.
        await Sales.Indexes.CreateOneAsync(
            new CreateIndexModel<Sale>(
                Builders<Sale>.IndexKeys
                    .Ascending(sale => sale.TenantId)
                    .Ascending(sale => sale.Status)
                    .Ascending(sale => sale.SoldAt)),
            cancellationToken: cancellationToken);

        // What one document moved: the units a sale took, to put them back when it is cancelled.
        await StockMovements.Indexes.CreateOneAsync(
            new CreateIndexModel<StockMovement>(
                Builders<StockMovement>.IndexKeys
                    .Ascending(movement => movement.TenantId)
                    .Ascending(movement => movement.DocumentId)),
            cancellationToken: cancellationToken);

        // Recent counts, and the open one - at most one per shop.
        await StockCounts.Indexes.CreateOneAsync(
            new CreateIndexModel<StockCount>(
                Builders<StockCount>.IndexKeys
                    .Ascending(count => count.TenantId)
                    .Descending(count => count.StartedAt)),
            cancellationToken: cancellationToken);

        // The active rules of a shop: read on every price quote.
        await DiscountRules.Indexes.CreateOneAsync(
            new CreateIndexModel<DiscountRule>(
                Builders<DiscountRule>.IndexKeys
                    .Ascending(rule => rule.TenantId)
                    .Ascending(rule => rule.Active)),
            cancellationToken: cancellationToken);

        await Suppliers.Indexes.CreateOneAsync(
            new CreateIndexModel<Supplier>(
                Builders<Supplier>.IndexKeys
                    .Ascending(supplier => supplier.TenantId)
                    .Ascending(supplier => supplier.Name)),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="work"/> inside a multi-document transaction: every write it makes
    /// lands, or none does. Needs a replica set - which is why the project requires one.
    /// </summary>
    /// <remarks>
    /// The driver retries the whole callback on transient errors (two transactions touching
    /// the same document), so <paramref name="work"/> must only write what it was given,
    /// never compute from reads outside the session.
    /// </remarks>
    public async Task InTransactionAsync(
        Func<IClientSessionHandle, CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        using var session = await Client.StartSessionAsync(cancellationToken: cancellationToken);

        await session.WithTransactionAsync(
            async (transaction, token) =>
            {
                await work(transaction, token);
                return true;
            },
            cancellationToken: cancellationToken);
    }
}
