using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Domain.Stock;

namespace Storage.Infrastructure.Persistence;

public sealed class MongoCountStore(MongoStorageContext context, ITenantContext tenant) : ICountStore
{
    private const string ItemsField = "Items";
    private const string ItemProductField = "Items.ProductId";

    // The positional "$" points at the item the filter matched on ProductId.
    private const string MatchedItemQuantityField = "Items.$.CountedQuantity";

    private FilterDefinition<StockCount> OfThisShop =>
        Builders<StockCount>.Filter.Eq(count => count.TenantId, tenant.TenantId);

    public async Task<StockCount?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        await context.StockCounts
            .Find(OfThisShop & Builders<StockCount>.Filter.Eq(count => count.Id, id))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<StockCount?> FindOpenAsync(CancellationToken cancellationToken = default) =>
        await context.StockCounts
            .Find(OfThisShop & Builders<StockCount>.Filter.Eq(count => count.Status, StockCountStatus.Open))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<Paged<StockCount>> ListAsync(PageRequest page, CancellationToken cancellationToken = default) =>
        await context.StockCounts.PageAsync(
            OfThisShop,
            Builders<StockCount>.Sort.Descending(count => count.StartedAt).Descending(count => count.Id),
            page,
            cancellationToken);

    public async Task AddAsync(StockCount count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(count);

        if (count.TenantId != tenant.TenantId)
        {
            throw new InvalidOperationException("Attempted to write a count that belongs to another tenant.");
        }

        await context.StockCounts.InsertOneAsync(count, options: null, cancellationToken);
    }

    public Task<bool> SetCountedAsync(Guid countId, Guid productId, int quantity, CancellationToken cancellationToken = default) =>
        UpsertItemAsync(
            countId,
            productId,
            Builders<StockCount>.Update.Set(MatchedItemQuantityField, quantity),
            new CountedItem(productId, quantity),
            cancellationToken);

    public Task<bool> AddCountedAsync(Guid countId, Guid productId, int units, CancellationToken cancellationToken = default) =>
        UpsertItemAsync(
            countId,
            productId,
            Builders<StockCount>.Update.Inc(MatchedItemQuantityField, units),
            new CountedItem(productId, units),
            cancellationToken);

    /// <summary>
    /// Changes one product's item in place, or appends it if it is the first scan of that
    /// product - each as a single atomic update, never a read-modify-write of the whole count.
    /// </summary>
    /// <remarks>
    /// If two people scan the same new product at the same instant, one append wins and the
    /// other's append matches nothing - its second attempt then finds the item and updates
    /// it. Nothing is lost either way. Every write bumps the version closing checks.
    /// </remarks>
    private async Task<bool> UpsertItemAsync(
        Guid countId,
        Guid productId,
        UpdateDefinition<StockCount> changeExisting,
        CountedItem appendIfMissing,
        CancellationToken cancellationToken)
    {
        var openCount = OfThisShop
            & Builders<StockCount>.Filter.Eq(count => count.Id, countId)
            & Builders<StockCount>.Filter.Eq(count => count.Status, StockCountStatus.Open);

        var bumpVersion = Builders<StockCount>.Update.Inc(count => count.Version, 1L);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await context.StockCounts.UpdateOneAsync(
                openCount & Builders<StockCount>.Filter.Eq(ItemProductField, productId),
                Builders<StockCount>.Update.Combine(changeExisting, bumpVersion),
                cancellationToken: cancellationToken);

            if (existing.MatchedCount > 0)
            {
                return true;
            }

            var appended = await context.StockCounts.UpdateOneAsync(
                openCount & Builders<StockCount>.Filter.Ne(ItemProductField, productId),
                Builders<StockCount>.Update.Combine(
                    Builders<StockCount>.Update.Push(ItemsField, appendIfMissing),
                    bumpVersion),
                cancellationToken: cancellationToken);

            if (appended.MatchedCount > 0)
            {
                return true;
            }
        }

        // Neither found nor appended twice over: the count is closed, cancelled or not ours.
        return false;
    }
}
