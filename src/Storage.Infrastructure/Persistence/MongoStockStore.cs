using MongoDB.Bson;
using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Infrastructure.Persistence;

public sealed class MongoStockStore(MongoStorageContext context, ITenantContext tenant) : IStockStore
{
    private FilterDefinition<Batch> BatchesOfThisShop =>
        Builders<Batch>.Filter.Eq(batch => batch.TenantId, tenant.TenantId);

    public async Task<IReadOnlyList<Batch>> ListBatchesAsync(
        Guid productId,
        bool availableOnly,
        CancellationToken cancellationToken = default)
    {
        var filter = BatchesOfThisShop & Builders<Batch>.Filter.Eq(batch => batch.ProductId, productId);

        if (availableOnly)
        {
            filter &= Builders<Batch>.Filter.Eq(batch => batch.Status, BatchStatus.Available);
        }

        return await context.Batches
            .Find(filter)
            .SortBy(batch => batch.ExpiryDate)
            .ThenBy(batch => batch.ReceivedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Batch>> ListExpiredBatchesAsync(
        DateOnly shopDate,
        CancellationToken cancellationToken = default)
    {
        var filter = BatchesOfThisShop
            & Builders<Batch>.Filter.Eq(batch => batch.Status, BatchStatus.Available)
            & Builders<Batch>.Filter.Lt(batch => batch.ExpiryDate, shopDate);

        return await context.Batches.Find(filter).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Batch>> ListExpiringBatchesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var filter = BatchesOfThisShop
            & Builders<Batch>.Filter.Eq(batch => batch.Status, BatchStatus.Available)
            & Builders<Batch>.Filter.Gte(batch => batch.ExpiryDate, from)
            & Builders<Batch>.Filter.Lte(batch => batch.ExpiryDate, to);

        return await context.Batches
            .Find(filter)
            .SortBy(batch => batch.ExpiryDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MovementTotal>> SumMovementsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<MovementType> types,
        CancellationToken cancellationToken = default)
    {
        // OccurredAt is ISO-8601 text, always written in UTC, so a range on it compares
        // chronologically - which is why every timestamp is stored in UTC.
        var filter = Builders<StockMovement>.Filter.Eq(movement => movement.TenantId, tenant.TenantId)
            & Builders<StockMovement>.Filter.In(movement => movement.Type, types)
            & Builders<StockMovement>.Filter.Gte(movement => movement.OccurredAt, from.ToUniversalTime())
            & Builders<StockMovement>.Filter.Lt(movement => movement.OccurredAt, to.ToUniversalTime());

        var groups = await context.StockMovements
            .Aggregate()
            .Match(filter)
            .Group(new BsonDocument
            {
                { "_id", new BsonDocument { { "ProductId", "$ProductId" }, { "Type", "$Type" } } },
                { "Quantity", new BsonDocument("$sum", "$Quantity") },
                { "Value", new BsonDocument("$sum", new BsonDocument("$multiply", new BsonArray { "$Quantity", "$UnitCost" })) },
            })
            .ToListAsync(cancellationToken);

        return groups
            .Select(group => new MovementTotal(
                group["_id"]["ProductId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard),
                Enum.Parse<MovementType>(group["_id"]["Type"].AsString),
                group["Quantity"].ToInt32(),
                Money.FromCents(group["Value"].ToInt64())))
            .ToArray();
    }

    public async Task<IReadOnlyList<StockMovement>> ListMovementsAsync(
        Guid productId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<StockMovement>.Filter.Eq(movement => movement.TenantId, tenant.TenantId)
            & Builders<StockMovement>.Filter.Eq(movement => movement.ProductId, productId);

        return await context.StockMovements
            .Find(filter)
            .SortByDescending(movement => movement.OccurredAt)
            .Limit(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GoodsReceipt>> ListReceiptsAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        await context.GoodsReceipts
            .Find(Builders<GoodsReceipt>.Filter.Eq(receipt => receipt.TenantId, tenant.TenantId))
            .SortByDescending(receipt => receipt.ReceivedAt)
            .Limit(limit)
            .ToListAsync(cancellationToken);

    public async Task<GoodsReceipt?> FindReceiptAsync(Guid id, CancellationToken cancellationToken = default) =>
        await context.GoodsReceipts
            .Find(Builders<GoodsReceipt>.Filter.Eq(receipt => receipt.TenantId, tenant.TenantId)
                & Builders<GoodsReceipt>.Filter.Eq(receipt => receipt.Id, id))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, StockLevel>> LevelsAsync(
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<Guid, StockLevel>();
        }

        var levels = productIds.ToDictionary(id => id, StockLevel.Empty);

        foreach (var level in await SumAvailableAsync(Builders<Batch>.Filter.In(batch => batch.ProductId, productIds), cancellationToken))
        {
            levels[level.ProductId] = level;
        }

        return levels;
    }

    public Task<IReadOnlyList<StockLevel>> AllLevelsAsync(CancellationToken cancellationToken = default) =>
        SumAvailableAsync(Builders<Batch>.Filter.Empty, cancellationToken);

    /// <summary>
    /// Balance, value and nearest expiry per product, summed where the data is: one round
    /// trip however many batches a product has.
    /// </summary>
    private async Task<IReadOnlyList<StockLevel>> SumAvailableAsync(
        FilterDefinition<Batch> products,
        CancellationToken cancellationToken)
    {
        var available = BatchesOfThisShop
            & Builders<Batch>.Filter.Eq(batch => batch.Status, BatchStatus.Available)
            & products;

        var groups = await context.Batches
            .Aggregate()
            .Match(available)
            .Group(new BsonDocument
            {
                { "_id", "$ProductId" },
                { "Quantity", new BsonDocument("$sum", "$RemainingQuantity") },
                {
                    "Value",
                    new BsonDocument("$sum", new BsonDocument("$multiply", new BsonArray { "$RemainingQuantity", "$UnitCost" }))
                },
                { "NextExpiry", new BsonDocument("$min", "$ExpiryDate") },
            })
            .ToListAsync(cancellationToken);

        return groups
            .Select(group => new StockLevel(
                group["_id"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard),
                group["Quantity"].ToInt32(),
                Money.FromCents(group["Value"].ToInt64()),
                group["NextExpiry"].IsBsonNull ? null : DateOnly.FromDateTime(group["NextExpiry"].ToUniversalTime())))
            .ToArray();
    }

    public async Task CommitAsync(StockChanges changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.IsEmpty)
        {
            return;
        }

        Guard(changes);

        await context.InTransactionAsync(
            async (session, token) =>
            {
                if (changes.NewBatches.Count > 0)
                {
                    await context.Batches.InsertManyAsync(session, changes.NewBatches, cancellationToken: token);
                }

                foreach (var changed in changes.ChangedBatches)
                {
                    await WriteChangedBatchAsync(session, changed, token);
                }

                if (changes.Movements.Count > 0)
                {
                    await context.StockMovements.InsertManyAsync(session, changes.Movements, cancellationToken: token);
                }

                foreach (var document in changes.Documents)
                {
                    await StockDocuments.InsertAsync(context, session, document, token);
                }

                foreach (var (document, expectedVersion) in changes.UpdatedDocuments)
                {
                    await StockDocuments.UpdateAsync(context, session, document, expectedVersion, token);
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Compare-and-set: the batch is written only if it still holds what it held when read.
    /// If another operation took units from it in between, nothing here is written - the
    /// exception aborts the whole transaction - and the caller gets a conflict to retry,
    /// instead of silently overwriting the other change.
    /// </summary>
    private async Task WriteChangedBatchAsync(
        IClientSessionHandle session,
        StockChanges.ChangedBatch changed,
        CancellationToken cancellationToken)
    {
        var unchangedSinceRead = BatchesOfThisShop
            & Builders<Batch>.Filter.Eq(batch => batch.Id, changed.Batch.Id)
            & Builders<Batch>.Filter.Eq(batch => batch.RemainingQuantity, changed.ExpectedRemaining)
            & Builders<Batch>.Filter.Eq(batch => batch.Status, changed.ExpectedStatus);

        var update = Builders<Batch>.Update
            .Set(batch => batch.RemainingQuantity, changed.Batch.RemainingQuantity)
            .Set(batch => batch.Status, changed.Batch.Status);

        var result = await context.Batches.UpdateOneAsync(
            session, unchangedSinceRead, update, cancellationToken: cancellationToken);

        if (result.MatchedCount == 0)
        {
            throw UseCaseException.Conflict(
                ErrorCodes.StockChangedConcurrently,
                "The stock changed while this operation was running; nothing was saved.");
        }
    }

    private void Guard(StockChanges changes)
    {
        var foreign = changes.NewBatches.Any(batch => batch.TenantId != tenant.TenantId)
            || changes.ChangedBatches.Any(changed => changed.Batch.TenantId != tenant.TenantId)
            || changes.Movements.Any(movement => movement.TenantId != tenant.TenantId)
            || changes.Documents.Any(document => StockDocuments.TenantOf(document) != tenant.TenantId)
            || changes.UpdatedDocuments.Any(updated => StockDocuments.TenantOf(updated.Document) != tenant.TenantId);

        if (foreign)
        {
            throw new InvalidOperationException("Attempted to write stock that belongs to another tenant.");
        }
    }
}
