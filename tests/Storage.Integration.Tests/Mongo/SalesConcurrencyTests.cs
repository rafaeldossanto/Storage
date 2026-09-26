using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Application.Sales;
using Storage.Domain.Accounts;
using Storage.Domain.Catalog;
using Storage.Domain.Common;
using Storage.Domain.Sales;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;
using Storage.Infrastructure.Persistence;

namespace Storage.Integration.Tests.Mongo;

/// <summary>
/// Several tills selling the same product at the same instant, against the real database:
/// the batch changes under each of them, and none of the cashiers should have to notice.
/// </summary>
public sealed class SalesConcurrencyTests(MongoFixture mongo)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Four_tills_selling_the_same_can_at_once_all_go_through()
    {
        var (db, shop, drink, batch) = await ShopWithStockAsync(units: 10);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Service(db, shop).RegisterAsync(Selling(drink, 1), Token)));

        var stored = await db.Batches.Find(Builders<Batch>.Filter.Eq(found => found.Id, batch.Id)).SingleAsync(Token);
        Assert.Equal(6, stored.RemainingQuantity);
        Assert.Equal(4, await db.Sales.CountDocumentsAsync(FilterDefinition<Sale>.Empty, cancellationToken: Token));
    }

    [Fact]
    public async Task When_the_shelf_runs_out_the_last_till_hears_so_not_a_conflict()
    {
        var (db, shop, drink, _) = await ShopWithStockAsync(units: 10);

        // Four sales of three cans from ten: one of them cannot be served.
        var attempts = Enumerable.Range(0, 4)
            .Select(_ => CaptureAsync(() => Service(db, shop).RegisterAsync(Selling(drink, 3), Token)))
            .ToArray();
        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(3, outcomes.Count(outcome => outcome is null));
        var refused = Assert.IsType<DomainException>(Assert.Single(outcomes, outcome => outcome is not null));
        Assert.Equal(DomainErrors.StockInsufficient, refused.Code);
    }

    private static RegisterSaleRequest Selling(Product product, int quantity) => new([new SaleItemRequest(product.Id, quantity)]);

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception refused)
        {
            return refused;
        }
    }

    // A fresh service per till, as each request gets its own in the API.
    private static SalesService Service(MongoStorageContext db, Tenant shop)
    {
        var tenant = new FixedTenant(shop.Id);
        var clock = new FixedClock(Now);

        return new SalesService(
            new MongoStockStore(db, tenant),
            new MongoSaleStore(db, tenant),
            new ProductRepository(db, tenant, clock),
            new CategoryRepository(db, tenant),
            new DiscountRuleRepository(db, tenant),
            new ShopCalendar(db, tenant, clock),
            tenant,
            new FixedUser(Guid.CreateVersion7()),
            clock);
    }

    private async Task<(MongoStorageContext Db, Tenant Shop, Product Drink, Batch Batch)> ShopWithStockAsync(int units)
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var shop = Tenant.Create("Mercadinho");
        await db.Tenants.InsertOneAsync(shop, cancellationToken: Token);

        var tenant = new FixedTenant(shop.Id);
        var beverages = Category.CreateRoot(shop.Id, "Bebidas");
        await new CategoryRepository(db, tenant).AddAsync(beverages, Token);

        var drink = Product.Create(shop.Id, "Energético 473ml", beverages.Id, UnitOfMeasure.Unit, Money.FromCents(899), Gtin.Parse("7891000000014"));
        await new ProductRepository(db, tenant, new FixedClock(Now)).AddAsync(drink, Token);

        var batch = Batch.Receive(shop.Id, drink.Id, units, Money.FromCents(500), null, Now.AddDays(-1));
        var changes = new StockChanges();
        changes.Add(batch);
        await new MongoStockStore(db, tenant).CommitAsync(changes, Token);

        return (db, shop, drink, batch);
    }
}
