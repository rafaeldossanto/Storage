using MongoDB.Bson;
using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Domain.Sales;
using Storage.Domain.ValueObjects;

namespace Storage.Infrastructure.Persistence;

public sealed class MongoSaleStore(MongoStorageContext context, ITenantContext tenant) : ISaleStore
{
    private FilterDefinition<Sale> OfThisShop => Builders<Sale>.Filter.Eq(sale => sale.TenantId, tenant.TenantId);

    public async Task<Sale?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        await context.Sales
            .Find(OfThisShop & Builders<Sale>.Filter.Eq(sale => sale.Id, id))
            .FirstOrDefaultAsync(cancellationToken);

    /// <remarks>
    /// One trip to the database whatever the period: the sums are made there, and a year of
    /// sales never travels to the application to be added up. <c>$dateTrunc</c> cuts time on
    /// the shop's clock, so a sale at 23:30 in São Paulo lands on its own day, not on the
    /// next one as it would in UTC.
    /// </remarks>
    public async Task<SalesSummary> SummarizeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        SalesBucket bucket,
        string timeZoneId,
        int topProducts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        var inPeriod = OfThisShop
            & Builders<Sale>.Filter.Eq(sale => sale.Status, SaleStatus.Completed)
            & Builders<Sale>.Filter.Gte(sale => sale.SoldAt, from)
            & Builders<Sale>.Filter.Lt(sale => sale.SoldAt, to);

        var unit = bucket switch
        {
            SalesBucket.Hour => "hour",
            SalesBucket.Day => "day",
            _ => "month",
        };

        var bucketStart = new BsonDocument("$dateTrunc", new BsonDocument
        {
            ["date"] = "$SoldAt",
            ["unit"] = unit,
            ["timezone"] = timeZoneId,
        });

        var facets = new BsonDocument("$facet", new BsonDocument
        {
            ["totals"] = new BsonArray { Group(BsonNull.Value), Counted() },
            ["buckets"] = new BsonArray { Group(bucketStart), Counted(), new BsonDocument("$sort", new BsonDocument("_id", 1)) },
            ["products"] = new BsonArray
            {
                new BsonDocument("$group", new BsonDocument
                {
                    ["_id"] = "$Lines.ProductId",
                    ["name"] = new BsonDocument("$last", "$Lines.ProductName"),
                    ["units"] = new BsonDocument("$sum", "$Lines.Quantity"),
                    ["revenue"] = new BsonDocument("$sum", LineRevenue),
                    ["cost"] = new BsonDocument("$sum", "$Lines.Cost"),
                }),
                new BsonDocument("$sort", new BsonDocument { ["revenue"] = -1, ["_id"] = 1 }),
                new BsonDocument("$limit", topProducts),
            },
        });

        var result = await context.Sales.Aggregate()
            .Match(inPeriod)
            .AppendStage<BsonDocument>(new BsonDocument("$unwind", "$Lines"))
            .AppendStage<BsonDocument>(facets)
            .FirstAsync(cancellationToken);

        var totals = result["totals"].AsBsonArray.Select(total => Figures(total.AsBsonDocument)).FirstOrDefault()
            ?? SalesFigures.None;

        var buckets = result["buckets"].AsBsonArray
            .Select(found => new SalesBucketFigures(
                new DateTimeOffset(found["_id"].ToUniversalTime(), TimeSpan.Zero),
                Figures(found.AsBsonDocument)))
            .ToArray();

        var products = result["products"].AsBsonArray
            .Select(found => new SoldProductFigures(
                found["_id"].AsBsonBinaryData.ToGuid(),
                found["name"].AsString,
                found["units"].ToInt32(),
                Money.FromCents(found["revenue"].ToInt64()),
                Money.FromCents(found["cost"].ToInt64())))
            .ToArray();

        return new SalesSummary(totals, buckets, products);
    }

    // What a line brought in. The price is stored per unit, as whole cents.
    private static BsonDocument LineRevenue =>
        new("$multiply", new BsonArray { "$Lines.UnitPrice", "$Lines.Quantity" });

    // Sums lines into one group; a sale with three lines still counts as one sale.
    private static BsonDocument Group(BsonValue key) =>
        new("$group", new BsonDocument
        {
            ["_id"] = key,
            ["sales"] = new BsonDocument("$addToSet", "$_id"),
            ["units"] = new BsonDocument("$sum", "$Lines.Quantity"),
            ["revenue"] = new BsonDocument("$sum", LineRevenue),
            ["cost"] = new BsonDocument("$sum", "$Lines.Cost"),
        });

    private static BsonDocument Counted() =>
        new("$project", new BsonDocument
        {
            ["sales"] = new BsonDocument("$size", "$sales"),
            ["units"] = 1,
            ["revenue"] = 1,
            ["cost"] = 1,
        });

    private static SalesFigures Figures(BsonDocument found) =>
        new(
            found["sales"].ToInt32(),
            found["units"].ToInt32(),
            Money.FromCents(found["revenue"].ToInt64()),
            Money.FromCents(found["cost"].ToInt64()));
}
