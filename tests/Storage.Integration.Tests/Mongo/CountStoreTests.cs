using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Stock;
using Storage.Infrastructure.Persistence;

namespace Storage.Integration.Tests.Mongo;

/// <summary>
/// Counting is done by several people at once, from several phones. These run against a
/// real database because atomicity is the database's promise, not the code's.
/// </summary>
public sealed class CountStoreTests(MongoFixture mongo)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Shop = Guid.CreateVersion7();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fifty_simultaneous_scans_of_one_product_count_fifty()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var count = await StartAsync(store);
        var drink = Guid.CreateVersion7();

        // The first scans race to create the item; the rest race to increment it.
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => store.AddCountedAsync(count.Id, drink, 1, Token)));

        var stored = await store.FindAsync(count.Id, Token);
        Assert.Equal(50, Assert.Single(stored!.Items).CountedQuantity);
        Assert.Equal(50, stored.Version);
    }

    [Fact]
    public async Task People_counting_different_products_never_overwrite_each_other()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var count = await StartAsync(store);
        var products = Enumerable.Range(0, 20).Select(_ => Guid.CreateVersion7()).ToArray();

        await Task.WhenAll(products.Select((product, index) => store.SetCountedAsync(count.Id, product, index + 1, Token)));

        var stored = await store.FindAsync(count.Id, Token);
        Assert.Equal(20, stored!.Items.Count);
        Assert.Equal(Enumerable.Range(1, 20).Sum(), stored.Items.Sum(item => item.CountedQuantity));
    }

    [Fact]
    public async Task A_scan_that_lands_after_the_count_was_read_blocks_the_close()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var stock = new MongoStockStore(db, new FixedTenant(Shop));
        var count = await StartAsync(store);
        var drink = Guid.CreateVersion7();
        await store.SetCountedAsync(count.Id, drink, 5, Token);

        var read = (await store.FindAsync(count.Id, Token))!;
        var readVersion = read.Version;

        // Someone scans one more while the owner is closing.
        await store.AddCountedAsync(count.Id, drink, 1, Token);

        read.Close(new Dictionary<Guid, int> { [drink] = 5 }, null, Guid.CreateVersion7(), Now);
        var changes = new StockChanges();
        changes.Update(read, readVersion);

        var conflict = await Assert.ThrowsAsync<UseCaseException>(() => stock.CommitAsync(changes, Token));

        Assert.Equal(ErrorCodes.StockChangedConcurrently, conflict.Code);
        var stored = (await store.FindAsync(count.Id, Token))!;
        Assert.Equal(StockCountStatus.Open, stored.Status);
        Assert.Equal(6, stored.Items.Single().CountedQuantity);
    }

    [Fact]
    public async Task A_closed_count_keeps_what_it_found_and_takes_no_more_scans()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var stock = new MongoStockStore(db, new FixedTenant(Shop));
        var count = await StartAsync(store);
        var drink = Guid.CreateVersion7();
        await store.SetCountedAsync(count.Id, drink, 8, Token);

        var read = (await store.FindAsync(count.Id, Token))!;
        var version = read.Version;
        read.Close(new Dictionary<Guid, int> { [drink] = 10 }, "Duas latas furtadas", Guid.CreateVersion7(), Now);
        var changes = new StockChanges();
        changes.Update(read, version);
        await stock.CommitAsync(changes, Token);

        var stored = (await store.FindAsync(count.Id, Token))!;
        Assert.Equal(StockCountStatus.Closed, stored.Status);
        Assert.Equal("Duas latas furtadas", stored.Justification);
        Assert.Equal(-2, stored.Items.Single().Difference);
        Assert.Equal(10, stored.Items.Single().SystemQuantity);

        Assert.False(await store.AddCountedAsync(count.Id, drink, 1, Token));
        Assert.Null(await store.FindOpenAsync(Token));
    }

    [Fact]
    public async Task A_product_counted_in_any_count_is_known_to_have_been_counted()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var count = await StartAsync(store);
        var counted = Guid.CreateVersion7();
        await store.SetCountedAsync(count.Id, counted, 0, Token);

        Assert.True(await store.AnyCountedAsync(counted, Token));
        Assert.False(await store.AnyCountedAsync(Guid.CreateVersion7(), Token));
        Assert.False(await new MongoCountStore(db, new FixedTenant(Guid.CreateVersion7())).AnyCountedAsync(counted, Token));
    }

    [Fact]
    public async Task Another_shops_count_cannot_be_scanned_into()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var count = await StartAsync(Store(db));

        var theirs = new MongoCountStore(db, new FixedTenant(Guid.CreateVersion7()));

        Assert.False(await theirs.AddCountedAsync(count.Id, Guid.CreateVersion7(), 1, Token));
        Assert.Null(await theirs.FindAsync(count.Id, Token));
    }

    private static MongoCountStore Store(MongoStorageContext db) => new(db, new FixedTenant(Shop));

    private static async Task<StockCount> StartAsync(MongoCountStore store)
    {
        var count = StockCount.Start(Shop, null, Guid.CreateVersion7(), Now);
        await store.AddAsync(count, Token);
        return count;
    }
}
