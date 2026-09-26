using Storage.Application.Abstractions;
using Storage.Domain.Catalog;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;
using Storage.Infrastructure.Persistence;

namespace Storage.Integration.Tests.Mongo;

/// <summary>
/// Paging against the real database: the order a person expects, and every document
/// reachable exactly once however the pages are walked.
/// </summary>
public sealed class PagingTests(MongoFixture mongo)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Shop = Guid.CreateVersion7();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Products_come_in_Portuguese_alphabetical_order_page_by_page()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var (products, beverages) = await ShopWithBeveragesAsync(db);

        // A byte-order sort would give Banana, Suco, abacaxi, Água: capitals first, the
        // accented initial last of all.
        string[] names = ["Suco de uva", "Água mineral", "abacaxi em calda", "Banana prata"];
        for (var i = 0; i < names.Length; i++)
        {
            await products.AddAsync(Product.Create(
                Shop, names[i], beverages.Id, UnitOfMeasure.Unit, Money.FromCents(500), Gtin.Parse(Barcodes[i])), Token);
        }

        var first = await products.ListByCategoryAsync(beverages, includeDescendants: true, PageRequest.Of(1, 2), Token);
        var second = await products.ListByCategoryAsync(beverages, includeDescendants: true, PageRequest.Of(2, 2), Token);

        Assert.Equal(["abacaxi em calda", "Água mineral"], first.Items.Select(product => product.Name));
        Assert.Equal(["Banana prata", "Suco de uva"], second.Items.Select(product => product.Name));
        Assert.Equal(4, second.Total);
        Assert.Equal(2, second.TotalPages);
    }

    [Fact]
    public async Task Products_with_the_same_name_each_show_on_exactly_one_page()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var (products, beverages) = await ShopWithBeveragesAsync(db);
        foreach (var barcode in Barcodes)
        {
            await products.AddAsync(Product.Create(
                Shop, "Refrigerante 2L", beverages.Id, UnitOfMeasure.Unit, Money.FromCents(900), Gtin.Parse(barcode)), Token);
        }

        var seen = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var slice = await products.ListByCategoryAsync(beverages, includeDescendants: true, PageRequest.Of(page, 2), Token);
            seen.AddRange(slice.Items.Select(product => product.Id));
        }

        Assert.Equal(Barcodes.Length, seen.Distinct().Count());
        Assert.Equal(Barcodes.Length, seen.Count);
    }

    [Fact]
    public async Task Old_deliveries_stay_reachable()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var stock = new MongoStockStore(db, new FixedTenant(Shop));
        var drink = Product.Create(
            Shop, "Energético 473ml", Guid.CreateVersion7(), UnitOfMeasure.Unit, Money.FromCents(899), Gtin.Parse(Barcodes[0]));
        drink.SetExpiryTracking(false);

        for (var day = 0; day < 5; day++)
        {
            var at = Now.AddDays(day);
            var receipt = GoodsReceipt.Open(Shop, supplierId: null, $"NF {day}", note: null, Guid.CreateVersion7(), at);
            var batch = receipt.Receive(drink, Gtin.Parse(Barcodes[0]), 1, Money.FromCents(100), null, DateOnly.FromDateTime(at.UtcDateTime));

            var changes = new StockChanges();
            changes.Add(batch);
            changes.Record(StockMovement.Receipt(batch, at, receipt.UserId, receipt.Id));
            changes.Attach(receipt);
            await stock.CommitAsync(changes, Token);
        }

        var oldest = await stock.ListReceiptsAsync(PageRequest.Of(3, 2), Token);

        Assert.Equal(["NF 0"], oldest.Items.Select(receipt => receipt.InvoiceNumber));
        Assert.Equal(5, oldest.Total);
    }

    private static readonly string[] Barcodes = ["7891000000014", "7891000000021", "7891000000038", "7891000000045", "7891000000052"];

    private static async Task<(ProductRepository Products, Category Beverages)> ShopWithBeveragesAsync(MongoStorageContext db)
    {
        var tenant = new FixedTenant(Shop);
        var beverages = Category.CreateRoot(Shop, "Bebidas");
        await new CategoryRepository(db, tenant).AddAsync(beverages, Token);

        return (new ProductRepository(db, tenant, new FixedClock(Now)), beverages);
    }
}
