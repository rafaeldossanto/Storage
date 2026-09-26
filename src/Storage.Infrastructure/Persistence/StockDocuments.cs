using MongoDB.Driver;

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
            // A type nobody taught this switch about is a bug to find at once, not data to
            // drop on the floor.
            _ => throw new NotSupportedException(
                $"No collection is configured for stock documents of type {document.GetType().Name}."),
        };
}
