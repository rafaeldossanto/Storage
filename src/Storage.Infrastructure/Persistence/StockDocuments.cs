using MongoDB.Driver;
using Storage.Application.Errors;
using Storage.Domain.Stock;

namespace Storage.Infrastructure.Persistence;

/// <summary>
/// Writes the documents that travel with a stock commit - a goods receipt, a count - into
/// the right collection, inside the same transaction as the batches and movements.
/// </summary>
internal static class StockDocuments
{
    public static Task InsertAsync(
        MongoStorageContext context,
        IClientSessionHandle session,
        object document,
        CancellationToken cancellationToken) =>
        document switch
        {
            GoodsReceipt receipt => context.GoodsReceipts.InsertOneAsync(
                session, receipt, cancellationToken: cancellationToken),

            // A type nobody taught this switch about is a bug to find at once, not data to
            // drop on the floor.
            _ => throw new NotSupportedException(
                $"No collection is configured for stock documents of type {document.GetType().Name}."),
        };

    /// <summary>
    /// Rewrites an existing document only if its version is still the one that was read. A
    /// scan that landed on a count after it was read for closing moves the version, and the
    /// whole commit - adjustments included - is refused instead of closing without it.
    /// </summary>
    public static async Task UpdateAsync(
        MongoStorageContext context,
        IClientSessionHandle session,
        object document,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        switch (document)
        {
            case StockCount count:
                var unchanged = Builders<StockCount>.Filter.Eq(stored => stored.Id, count.Id)
                    & Builders<StockCount>.Filter.Eq(stored => stored.TenantId, count.TenantId)
                    & Builders<StockCount>.Filter.Eq(stored => stored.Version, expectedVersion);

                var close = Builders<StockCount>.Update
                    .Set(stored => stored.Status, count.Status)
                    .Set(stored => stored.ClosedBy, count.ClosedBy)
                    .Set(stored => stored.ClosedAt, count.ClosedAt)
                    .Set(stored => stored.Justification, count.Justification)
                    .Set("Items", count.Items)
                    .Inc(stored => stored.Version, 1L);

                var result = await context.StockCounts.UpdateOneAsync(session, unchanged, close, cancellationToken: cancellationToken);

                if (result.MatchedCount == 0)
                {
                    throw UseCaseException.Conflict(
                        ErrorCodes.StockChangedConcurrently,
                        "The count changed while it was being closed; nothing was saved.");
                }

                break;

            default:
                throw new NotSupportedException(
                    $"Updating stock documents of type {document.GetType().Name} is not supported.");
        }
    }

    public static Guid TenantOf(object document) => document switch
    {
        GoodsReceipt receipt => receipt.TenantId,
        StockCount count => count.TenantId,
        _ => throw new NotSupportedException($"Unknown stock document type {document.GetType().Name}."),
    };
}
