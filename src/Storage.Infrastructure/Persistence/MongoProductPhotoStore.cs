using MongoDB.Bson;
using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;

namespace Storage.Infrastructure.Persistence;

/// <summary>
/// Photos in two collections: what is known about each code, small and read with every
/// page of products, and the pictures themselves, read only when a browser asks for one.
/// </summary>
/// <remarks>
/// A packshot is a few dozen kilobytes, far under MongoDB's 16 MB per document, so the
/// bytes live in the database for now. Moving them to object storage behind a CDN later
/// only changes this class and the photo route.
/// </remarks>
internal sealed class MongoProductPhotoStore(MongoStorageContext context, TimeProvider clock) : IProductPhotoStore
{
    private static readonly FilterDefinitionBuilder<ProductPhoto> Filter = Builders<ProductPhoto>.Filter;

    public async Task RequestAsync(IReadOnlyCollection<Gtin> gtins, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gtins);

        var now = clock.GetUtcNow();

        // One upsert per code that only writes when it inserts: a code already known keeps
        // its status, its photo and its retry schedule.
        var writes = gtins
            .Where(gtin => !gtin.IsInternal)
            .Distinct()
            .Select(gtin =>
            {
                var fresh = ProductPhoto.Request(gtin, now).ToBsonDocument();
                fresh.Remove("_id");

                return new UpdateOneModel<ProductPhoto>(
                    Filter.Eq(photo => photo.Gtin, gtin),
                    new BsonDocument("$setOnInsert", fresh)) { IsUpsert = true };
            })
            .ToArray();

        if (writes.Length == 0)
        {
            return;
        }

        try
        {
            await context.ProductPhotos.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
        }
        catch (MongoBulkWriteException exception)
            when (exception.WriteErrors.All(error => error.Category == ServerErrorCategory.DuplicateKey))
        {
            // Two shops registering the same can at the same instant: both upserts tried to
            // insert, one won. The code is requested either way.
        }
    }

    public async Task RequestAllCatalogedAsync(CancellationToken cancellationToken = default)
    {
        // Every code of every shop, minus the codes already known, in one pass on the server.
        var unknown = await context.Database
            .GetCollection<BsonDocument>(MongoStorageContext.ProductsCollection)
            .Aggregate()
            .Unwind("Packagings")
            .Group(new BsonDocument("_id", "$" + MongoStorageContext.PackagingGtinField))
            .Lookup(MongoStorageContext.ProductPhotosCollection, "_id", "_id", "known")
            .Match(new BsonDocument("known", new BsonDocument("$size", 0)))
            .Project(new BsonDocument("_id", 1))
            .ToListAsync(cancellationToken);

        var gtins = unknown
            .Select(document => Gtin.Parse(document["_id"].AsString))
            .ToArray();

        await RequestAsync(gtins, cancellationToken);
    }

    public async Task<ProductPhoto?> ClaimNextAsync(
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken = default) =>
        await context.ProductPhotos.FindOneAndUpdateAsync(
            Filter.Ne(photo => photo.Status, ProductPhotoStatus.Ready) & Filter.Lte(photo => photo.NextAttemptAt, now),
            Builders<ProductPhoto>.Update.Set(photo => photo.NextAttemptAt, now + lease),
            new FindOneAndUpdateOptions<ProductPhoto>
            {
                Sort = Builders<ProductPhoto>.Sort.Ascending(photo => photo.NextAttemptAt),
                ReturnDocument = ReturnDocument.After,
            },
            cancellationToken);

    public async Task SaveAsync(ProductPhoto photo, PhotoFile? file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);

        // The picture first: a code marked ready must always have one to serve. A crash in
        // between leaves a picture nobody points at yet, overwritten on the next attempt.
        if (file is not null)
        {
            var stored = new StoredPhotoFile
            {
                Gtin = photo.Gtin.Value,
                Version = file.Version,
                ContentType = file.ContentType,
                Content = file.Content,
                StoredAt = clock.GetUtcNow(),
            };

            await context.ProductPhotoFiles.ReplaceOneAsync(
                candidate => candidate.Gtin == stored.Gtin,
                stored,
                new ReplaceOptions { IsUpsert = true },
                cancellationToken);
        }

        await context.ProductPhotos.ReplaceOneAsync(
            Filter.Eq(candidate => candidate.Gtin, photo.Gtin),
            photo,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Gtin, ProductPhoto>> FindReadyAsync(
        IReadOnlyCollection<Gtin> gtins,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gtins);

        if (gtins.Count == 0)
        {
            return new Dictionary<Gtin, ProductPhoto>();
        }

        var ready = await context.ProductPhotos
            .Find(Filter.In(photo => photo.Gtin, gtins) & Filter.Eq(photo => photo.Status, ProductPhotoStatus.Ready))
            .ToListAsync(cancellationToken);

        return ready.ToDictionary(photo => photo.Gtin);
    }

    public async Task<PhotoFile?> OpenAsync(Gtin gtin, CancellationToken cancellationToken = default)
    {
        var stored = await context.ProductPhotoFiles
            .Find(candidate => candidate.Gtin == gtin.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return stored is null ? null : new PhotoFile(stored.Version, stored.ContentType, stored.Content);
    }
}

/// <summary>A stored packshot. Infrastructure's own shape: the domain never sees the bytes.</summary>
internal sealed class StoredPhotoFile
{
    public string Gtin { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public byte[] Content { get; set; } = [];

    public DateTimeOffset StoredAt { get; set; }
}
