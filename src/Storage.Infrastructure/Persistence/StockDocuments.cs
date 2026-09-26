using MongoDB.Driver;
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

    public static Guid TenantOf(object document) => document switch
    {
        GoodsReceipt receipt => receipt.TenantId,
        _ => throw new NotSupportedException($"Unknown stock document type {document.GetType().Name}."),
    };
}
