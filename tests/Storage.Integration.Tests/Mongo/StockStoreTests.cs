using MongoDB.Bson;
using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;
using Storage.Infrastructure.Persistence;

namespace Storage.Integration.Tests.Mongo;

/// <summary>
/// The ledger against a real replica set: what is committed together lands together, and a
/// stale read turns into a conflict instead of a lost update.
/// </summary>
public sealed class StockStoreTests(MongoFixture mongo)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Shop = Guid.CreateVersion7();
    private static readonly Guid Drink = Guid.CreateVersion7();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_receipt_lands_as_batch_and_movement_together()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var changes = Receipt(Drink, 12, 500, new DateOnly(2026, 12, 1));

        await store.CommitAsync(changes, Token);

        Assert.Single(await store.ListBatchesAsync(Drink, availableOnly: true, Token));
        var movement = Assert.Single((await store.ListMovementsAsync(Drink, PageRequest.First(), Token)).Items);
        Assert.Equal(12, movement.Quantity);
    }

    [Fact]
    public async Task When_one_part_of_a_commit_fails_nothing_of_it_is_written()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var existing = await SeedAsync(store, Drink, 10, 500);

        // Someone else takes units from the batch after this operation read it...
        var concurrent = new StockChanges();
        var theirCopy = (await store.ListBatchesAsync(Drink, true, Token)).Single();
        concurrent.Change(theirCopy, batch => batch.Take(4));
        await store.CommitAsync(concurrent, Token);

        // ...so this commit, built on the stale read, must fail - and take down with it the
        // brand-new batch and the movement it was also carrying.
        var stale = Receipt(Guid.CreateVersion7(), 5, 300, null);
        stale.Change(existing, batch => batch.Take(2));
        stale.Record(StockMovement.Outflow(MovementType.DamageLoss, existing, 2, Now, userId: null));

        var conflict = await Assert.ThrowsAsync<UseCaseException>(() => store.CommitAsync(stale, Token));

        Assert.Equal(ErrorKind.Conflict, conflict.Kind);
        Assert.Equal(ErrorCodes.StockChangedConcurrently, conflict.Code);
        Assert.Equal(1, await db.Batches.CountDocumentsAsync(FilterDefinition<Batch>.Empty, cancellationToken: Token));
        Assert.Equal(1, await db.StockMovements.CountDocumentsAsync(FilterDefinition<StockMovement>.Empty, cancellationToken: Token));
    }

    [Fact]
    public async Task A_stale_write_never_overwrites_the_change_that_won()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        await SeedAsync(store, Drink, 10, 500);

        var first = (await store.ListBatchesAsync(Drink, true, Token)).Single();
        var second = (await store.ListBatchesAsync(Drink, true, Token)).Single();

        var winner = new StockChanges();
        winner.Change(first, batch => batch.Take(3));
        await store.CommitAsync(winner, Token);

        var loser = new StockChanges();
        loser.Change(second, batch => batch.Take(1));
        await Assert.ThrowsAsync<UseCaseException>(() => store.CommitAsync(loser, Token));

        Assert.Equal(7, (await store.ListBatchesAsync(Drink, true, Token)).Single().RemainingQuantity);
    }

    [Fact]
    public async Task Levels_are_summed_in_the_database_from_available_batches_only()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var noStock = Guid.CreateVersion7();
        await SeedAsync(store, Drink, 10, 500, new DateOnly(2026, 12, 1));
        await SeedAsync(store, Drink, 30, 600, new DateOnly(2026, 10, 15));

        var spoiled = await SeedAsync(store, Drink, 5, 400, new DateOnly(2026, 9, 1));
        var expiry = new StockChanges();
        expiry.Change(spoiled, batch => batch.Expire());
        await store.CommitAsync(expiry, Token);

        var levels = await store.LevelsAsync([Drink, noStock], Token);

        Assert.Equal(40, levels[Drink].Quantity);
        Assert.Equal(23000, levels[Drink].Value.Cents);
        Assert.Equal(575, levels[Drink].AverageCost.Cents);
        Assert.Equal(new DateOnly(2026, 10, 15), levels[Drink].NextExpiry);
        Assert.Equal(0, levels[noStock].Quantity);
        Assert.Null(levels[noStock].NextExpiry);
    }

    [Fact]
    public async Task The_whole_shop_is_summed_per_product_in_one_query()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var water = Guid.CreateVersion7();
        await SeedAsync(store, Drink, 10, 500);
        await SeedAsync(store, Drink, 5, 600);
        await SeedAsync(store, water, 20, 100);

        var otherShop = Guid.CreateVersion7();
        var theirs = new StockChanges();
        theirs.Add(Batch.Receive(otherShop, Drink, 99, Money.FromCents(999), null, Now));
        await new MongoStockStore(db, new FixedTenant(otherShop)).CommitAsync(theirs, Token);

        var levels = await store.AllLevelsAsync(Token);

        Assert.Equal(2, levels.Count);
        Assert.Equal(15, levels.Single(level => level.ProductId == Drink).Quantity);
        Assert.Equal(8000 + 2000, levels.Sum(level => level.Value.Cents));
    }

    [Fact]
    public async Task Another_shops_stock_is_neither_counted_nor_listed()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        await SeedAsync(Store(db), Drink, 10, 500);

        var otherShop = new MongoStockStore(db, new FixedTenant(Guid.CreateVersion7()));

        Assert.Equal(0, (await otherShop.LevelsAsync([Drink], Token))[Drink].Quantity);
        Assert.Empty(await otherShop.ListBatchesAsync(Drink, false, Token));
        Assert.Empty((await otherShop.ListMovementsAsync(Drink, PageRequest.First(), Token)).Items);
    }

    [Fact]
    public async Task Expired_batches_are_found_by_date_on_the_shops_calendar()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        await SeedAsync(store, Drink, 10, 500, new DateOnly(2026, 9, 17));
        await SeedAsync(store, Drink, 10, 500, new DateOnly(2026, 9, 18));
        await SeedAsync(store, Drink, 10, 500, expiry: null);

        var expired = await store.ListExpiredBatchesAsync(new DateOnly(2026, 9, 18), Token);

        // The 18th is still sellable on the 18th; goods without a date never show up.
        var batch = Assert.Single(expired);
        Assert.Equal(new DateOnly(2026, 9, 17), batch.ExpiryDate);
    }

    [Fact]
    public async Task A_batch_is_stored_with_a_real_date_cents_and_status_by_name()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        await SeedAsync(Store(db), Drink, 10, 500, new DateOnly(2026, 12, 1));

        var raw = await db.Database.GetCollection<BsonDocument>(MongoStorageContext.BatchesCollection)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .SingleAsync(Token);

        Assert.Equal(BsonType.DateTime, raw["ExpiryDate"].BsonType);
        Assert.Equal(BsonType.Int64, raw["UnitCost"].BsonType);
        Assert.Equal("Available", raw["Status"].AsString);
    }

    [Fact]
    public async Task Stock_of_another_shop_cannot_be_written()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var foreign = new StockChanges();
        foreign.Add(Batch.Receive(Guid.CreateVersion7(), Drink, 1, Money.FromCents(100), null, Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(db).CommitAsync(foreign, Token));
    }

    [Fact]
    public async Task A_goods_receipt_is_committed_with_its_batches_and_read_back_whole()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var drink = Storage.Domain.Catalog.Product.Create(Shop, "Energético 473ml", Guid.CreateVersion7(),
            Storage.Domain.Catalog.UnitOfMeasure.Unit, Money.FromCents(899), Gtin.Parse("7891000000014"));
        drink.AddPackaging(Gtin.Parse("17891000000011"), "Fardo 12", 12);

        var receipt = GoodsReceipt.Open(Shop, supplierId: null, "NF 1234", "Entrega da manhã", Guid.CreateVersion7(), Now);
        var batch = receipt.Receive(drink, Gtin.Parse("17891000000011"), 2, Money.FromCents(6000),
            new DateOnly(2026, 12, 1), new DateOnly(2026, 9, 26));

        var changes = new StockChanges();
        changes.Add(batch);
        changes.Record(StockMovement.Receipt(batch, Now, receipt.UserId, receipt.Id));
        changes.Attach(receipt);

        await store.CommitAsync(changes, Token);

        var stored = await store.FindReceiptAsync(receipt.Id, Token);
        Assert.NotNull(stored);
        var line = Assert.Single(stored.Lines);
        Assert.Equal(24, line.BaseUnits);
        Assert.Equal(500, line.UnitCost.Cents);
        Assert.Equal(Gtin.Parse("17891000000011"), line.Gtin);
        Assert.Equal(new DateOnly(2026, 12, 1), line.ExpiryDate);
        Assert.Equal(12000, stored.TotalCost.Cents);
        Assert.Equal("NF 1234", stored.InvoiceNumber);

        Assert.Single((await store.ListReceiptsAsync(PageRequest.First(), Token)).Items);
        Assert.Empty((await new MongoStockStore(db, new FixedTenant(Guid.CreateVersion7())).ListReceiptsAsync(PageRequest.First(), Token)).Items);
    }

    [Fact]
    public async Task Summing_movements_over_a_period_includes_its_start_and_excludes_its_end()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var batch = await SeedAsync(store, Drink, 100, 500);

        var start = new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.Zero);

        // Timestamps are stored as ISO text; the range has to compare them as instants.
        var changes = new StockChanges();
        foreach (var at in new[] { start.AddTicks(-1), start, start.AddDays(15).AddMilliseconds(250), end.AddTicks(-1), end })
        {
            changes.Record(StockMovement.Outflow(MovementType.DamageLoss, batch, 1, at, userId: null));
        }

        changes.Record(StockMovement.Outflow(MovementType.ReturnToSupplier, batch, 5, start.AddDays(1), userId: null));
        await store.CommitAsync(changes, Token);

        var totals = await store.SumMovementsAsync(start, end, [MovementType.DamageLoss], Token);

        var damage = Assert.Single(totals);
        Assert.Equal(-3, damage.Quantity);
        Assert.Equal(-1500, damage.Value.Cents);
        Assert.Equal(MovementType.DamageLoss, damage.Type);
    }

    [Fact]
    public async Task Batches_expiring_in_a_window_are_found_inclusive_at_both_ends()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        await SeedAsync(store, Drink, 1, 100, new DateOnly(2026, 9, 26));
        await SeedAsync(store, Drink, 1, 100, new DateOnly(2026, 10, 26));
        await SeedAsync(store, Drink, 1, 100, new DateOnly(2026, 10, 27));
        await SeedAsync(store, Drink, 1, 100, expiry: null);

        var expiring = await store.ListExpiringBatchesAsync(new DateOnly(2026, 9, 26), new DateOnly(2026, 10, 26), Token);

        Assert.Equal(2, expiring.Count);
    }

    private static MongoStockStore Store(MongoStorageContext db) => new(db, new FixedTenant(Shop));

    private static StockChanges Receipt(Guid product, int quantity, long unitCostCents, DateOnly? expiry)
    {
        var batch = Batch.Receive(Shop, product, quantity, Money.FromCents(unitCostCents), expiry, Now);
        var changes = new StockChanges();
        changes.Add(batch);
        changes.Record(StockMovement.Receipt(batch, Now, userId: null, receiptId: null));
        return changes;
    }

    private static async Task<Batch> SeedAsync(
        MongoStockStore store,
        Guid product,
        int quantity,
        long unitCostCents,
        DateOnly? expiry = null)
    {
        var changes = Receipt(product, quantity, unitCostCents, expiry);
        await store.CommitAsync(changes, Token);
        return changes.NewBatches[0];
    }
}
