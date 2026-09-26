using MongoDB.Bson;
using MongoDB.Driver;
using Storage.Domain.Accounts;
using Storage.Domain.Catalog;

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
    }
}
