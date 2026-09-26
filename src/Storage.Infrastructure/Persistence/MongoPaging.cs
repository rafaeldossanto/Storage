using MongoDB.Driver;
using Storage.Application.Abstractions;

namespace Storage.Infrastructure.Persistence;

internal static class MongoPaging
{
    /// <summary>
    /// One page of a query, and how many documents match in all.
    /// </summary>
    /// <remarks>
    /// The sort must end on a unique field - the id - so documents that tie on everything
    /// else keep one order between requests, and none shows on two pages or on none.
    /// </remarks>
    public static async Task<Paged<T>> PageAsync<T>(
        this IMongoCollection<T> collection,
        FilterDefinition<T> filter,
        SortDefinition<T> sort,
        PageRequest page,
        CancellationToken cancellationToken,
        FindOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        var total = await collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        if (page.Skip >= total)
        {
            return new Paged<T>([], page.Page, page.PageSize, total);
        }

        var items = await collection
            .Find(filter, options)
            .Sort(sort)
            .Skip(page.Skip)
            .Limit(page.PageSize)
            .ToListAsync(cancellationToken);

        return new Paged<T>(items, page.Page, page.PageSize, total);
    }
}
