using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Catalog;
using Storage.Domain.Sales;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;
using Storage.Infrastructure.Persistence;

namespace Storage.Integration.Tests.Mongo;

/// <summary>The sums behind the sales report, made by the database on the shop's clock.</summary>
public sealed class SaleStoreTests(MongoFixture mongo)
{
    private const string SaoPaulo = "America/Sao_Paulo";

    private static readonly Guid Shop = Guid.CreateVersion7();

    private static readonly Product Drink = Product.Create(
        Shop, "Energético 473ml", Guid.CreateVersion7(), UnitOfMeasure.Unit, Money.FromCents(899), Gtin.Parse("7891000000014"));

    private static readonly Product Soap = Product.Create(
        Shop, "Sabão em barra", Guid.CreateVersion7(), UnitOfMeasure.Unit, Money.FromCents(450), Gtin.Parse("7891000000021"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sale_late_at_night_counts_on_the_shops_own_day()
    {
        var db = await mongo.NewDatabaseAsync(Token);

        // 02:30 UTC on the 26th is still 23:30 on the 25th in São Paulo.
        await SellAsync(db, At(2026, 9, 26, 2, 30), (Drink, 2, 899, 500));
        await SellAsync(db, At(2026, 9, 26, 13, 0), (Drink, 1, 899, 500));

        var summary = await Store(db).SummarizeAsync(At(2026, 9, 1, 3, 0), At(2026, 10, 1, 3, 0), SalesBucket.Day, SaoPaulo, 10, Token);

        Assert.Equal(
            [At(2026, 9, 25, 3, 0), At(2026, 9, 26, 3, 0)],
            summary.Buckets.Select(bucket => bucket.Start));
        Assert.Equal([1798L, 899L], summary.Buckets.Select(bucket => bucket.Figures.Revenue.Cents));
    }

    [Fact]
    public async Task Totals_and_products_sum_revenue_cost_and_what_was_left()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        await SellAsync(db, At(2026, 9, 26, 13, 0), (Drink, 2, 899, 500), (Soap, 1, 450, 300));
        await SellAsync(db, At(2026, 9, 26, 15, 0), (Drink, 1, 809, 500));

        var summary = await Store(db).SummarizeAsync(At(2026, 9, 26, 3, 0), At(2026, 9, 27, 3, 0), SalesBucket.Hour, SaoPaulo, 10, Token);

        Assert.Equal(2, summary.Totals.Sales);
        Assert.Equal(4, summary.Totals.Units);
        Assert.Equal(2 * 899 + 450 + 809, summary.Totals.Revenue.Cents);
        Assert.Equal(3 * 500 + 300, summary.Totals.Cost.Cents);

        var drink = summary.Products[0];
        Assert.Equal("Energético 473ml", drink.Name);
        Assert.Equal(3, drink.Units);
        Assert.Equal(2 * 899 + 809 - 3 * 500, drink.Net.Cents);
    }

    [Fact]
    public async Task Cancelled_sales_and_other_shops_sales_do_not_count()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var kept = await SellAsync(db, At(2026, 9, 26, 13, 0), (Drink, 1, 899, 500));
        var undone = await SellAsync(db, At(2026, 9, 26, 13, 5), (Drink, 5, 899, 500));
        await CancelAsync(db, undone, At(2026, 9, 26, 13, 6));

        var theirs = Sale.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), At(2026, 9, 26, 14, 0));
        var theirBatch = Batch.Receive(theirs.TenantId, Drink.Id, 10, Money.FromCents(500), null, At(2026, 9, 1, 0, 0));
        theirs.AddLine(Drink, Money.FromCents(899), [(theirBatch, 7)]);
        var theirChanges = new StockChanges();
        theirChanges.Attach(theirs);
        await new MongoStockStore(db, new FixedTenant(theirs.TenantId)).CommitAsync(theirChanges, Token);

        var summary = await Store(db).SummarizeAsync(At(2026, 9, 26, 3, 0), At(2026, 9, 27, 3, 0), SalesBucket.Hour, SaoPaulo, 10, Token);

        Assert.Equal(1, summary.Totals.Units);
        Assert.Equal(kept.Total.Cents, summary.Totals.Revenue.Cents);
    }

    [Fact]
    public async Task A_sale_is_read_back_whole_and_cancelled_only_once()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var sale = await SellAsync(db, At(2026, 9, 26, 13, 0), (Drink, 2, 899, 500));

        var read = await Store(db).FindAsync(sale.Id, Token);
        Assert.NotNull(read);
        Assert.Equal(sale.SoldAt, read.SoldAt);
        Assert.Equal(1798, read.Total.Cents);
        Assert.Equal(1000, read.Cost.Cents);

        // Two cancellations read the same version; the second must not put stock back again.
        var first = (await Store(db).FindAsync(sale.Id, Token))!;
        var second = (await Store(db).FindAsync(sale.Id, Token))!;
        await CancelAsync(db, first, At(2026, 9, 26, 13, 1));

        var conflict = await Assert.ThrowsAsync<UseCaseException>(() => CancelAsync(db, second, At(2026, 9, 26, 13, 1)));

        Assert.Equal(ErrorCodes.StockChangedConcurrently, conflict.Code);
        Assert.Equal(SaleStatus.Cancelled, (await Store(db).FindAsync(sale.Id, Token))!.Status);
    }

    private static MongoSaleStore Store(MongoStorageContext db) => new(db, new FixedTenant(Shop));

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static async Task<Sale> SellAsync(
        MongoStorageContext db,
        DateTimeOffset at,
        params (Product Product, int Quantity, long PriceCents, long CostCents)[] lines)
    {
        var sale = Sale.Start(Shop, Guid.CreateVersion7(), at);

        foreach (var (product, quantity, price, cost) in lines)
        {
            var batch = Batch.Receive(Shop, product.Id, 100, Money.FromCents(cost), null, at.AddDays(-1));
            sale.AddLine(product, Money.FromCents(price), [(batch, quantity)]);
        }

        var changes = new StockChanges();
        changes.Attach(sale);
        await new MongoStockStore(db, new FixedTenant(Shop)).CommitAsync(changes, Token);
        return sale;
    }

    private static Task CancelAsync(MongoStorageContext db, Sale sale, DateTimeOffset at)
    {
        var version = sale.Version;
        sale.Cancel(Guid.CreateVersion7(), at);

        var changes = new StockChanges();
        changes.Update(sale, version);
        return new MongoStockStore(db, new FixedTenant(sale.TenantId)).CommitAsync(changes, Token);
    }
}
