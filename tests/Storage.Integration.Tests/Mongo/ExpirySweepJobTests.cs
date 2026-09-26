using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Storage.Api.Jobs;
using Storage.Api.Tenancy;
using Storage.Application;
using Storage.Domain.Accounts;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;
using Storage.Infrastructure;
using Storage.Infrastructure.Persistence;

namespace Storage.Integration.Tests.Mongo;

/// <summary>
/// The expiry job wired exactly as the API wires it, against a real database: every shop in
/// turn, each on its own calendar.
/// </summary>
public sealed class ExpirySweepJobTests(MongoFixture mongo)
{
    // 03:30 UTC on the 19th: already the 19th in São Paulo (UTC-3), still the 18th in Manaus (UTC-4).
    private static readonly DateTimeOffset Instant = new(2026, 9, 19, 3, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly The18th = new(2026, 9, 18);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_shop_expires_its_goods_on_its_own_calendar()
    {
        await using var provider = BuildServices();
        var db = await PrepareAsync(provider);

        var saoPaulo = await AddShopAsync(db, "Mercadinho Paulista", "America/Sao_Paulo");
        var manaus = await AddShopAsync(db, "Mercadinho Amazonense", "America/Manaus");
        var paulistaBatch = await SeedBatchAsync(db, saoPaulo, quantity: 8, unitCostCents: 500, The18th);
        var amazonenseBatch = await SeedBatchAsync(db, manaus, quantity: 8, unitCostCents: 500, The18th);

        var results = await provider.GetRequiredService<ExpirySweepJob>().RunOnceAsync(Token);

        // In São Paulo it is the 19th: the batch has passed its date.
        Assert.Equal(new(1, 8, 4000), results[saoPaulo.Id]);
        Assert.Equal(BatchStatus.Expired, (await FindBatchAsync(db, paulistaBatch.Id)).Status);

        // In Manaus it is still the 18th: sellable until midnight there.
        Assert.Equal(new(0, 0, 0), results[manaus.Id]);
        Assert.Equal(BatchStatus.Available, (await FindBatchAsync(db, amazonenseBatch.Id)).Status);
    }

    [Fact]
    public async Task What_expired_becomes_a_recorded_loss_not_a_deletion()
    {
        await using var provider = BuildServices();
        var db = await PrepareAsync(provider);
        var shop = await AddShopAsync(db, "Mercadinho Paulista", "America/Sao_Paulo");
        var batch = await SeedBatchAsync(db, shop, quantity: 8, unitCostCents: 500, The18th);

        await provider.GetRequiredService<ExpirySweepJob>().RunOnceAsync(Token);

        var loss = await db.StockMovements
            .Find(Builders<StockMovement>.Filter.Eq(movement => movement.Type, MovementType.ExpiryLoss))
            .SingleAsync(Token);

        Assert.Equal(batch.Id, loss.BatchId);
        Assert.Equal(-8, loss.Quantity);
        Assert.Equal(-4000, loss.Value.Cents);
        Assert.Null(loss.UserId);

        // The batch is still there, with its history intact.
        Assert.Equal(8, (await FindBatchAsync(db, batch.Id)).InitialQuantity);
    }

    [Fact]
    public async Task Running_twice_records_the_loss_once()
    {
        await using var provider = BuildServices();
        var db = await PrepareAsync(provider);
        var shop = await AddShopAsync(db, "Mercadinho Paulista", "America/Sao_Paulo");
        await SeedBatchAsync(db, shop, quantity: 8, unitCostCents: 500, The18th);

        var job = provider.GetRequiredService<ExpirySweepJob>();
        await job.RunOnceAsync(Token);
        var second = await job.RunOnceAsync(Token);

        Assert.Equal(new(0, 0, 0), second[shop.Id]);
        Assert.Equal(1, await db.StockMovements.CountDocumentsAsync(
            Builders<StockMovement>.Filter.Eq(movement => movement.Type, MovementType.ExpiryLoss), cancellationToken: Token));
    }

    [Fact]
    public async Task A_suspended_shop_is_left_alone()
    {
        await using var provider = BuildServices();
        var db = await PrepareAsync(provider);
        var shop = await AddShopAsync(db, "Mercadinho Suspenso", "America/Sao_Paulo", active: false);
        await SeedBatchAsync(db, shop, quantity: 8, unitCostCents: 500, The18th);

        var results = await provider.GetRequiredService<ExpirySweepJob>().RunOnceAsync(Token);

        Assert.False(results.ContainsKey(shop.Id));
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedClock(Instant));
        services.AddStoragePersistence(mongo.ConnectionString, $"test_{Guid.CreateVersion7():N}");
        services.AddStorageApplication();
        services.AddStorageTenancy();
        services.AddSingleton<ExpirySweepJob>();
        return services.BuildServiceProvider();
    }

    private static async Task<MongoStorageContext> PrepareAsync(ServiceProvider provider)
    {
        var db = provider.GetRequiredService<MongoStorageContext>();
        await db.EnsureIndexesAsync(Token);
        return db;
    }

    private static async Task<Tenant> AddShopAsync(MongoStorageContext db, string name, string timeZone, bool active = true)
    {
        var shop = Tenant.Create(name, timeZone);
        if (!active)
        {
            shop.Suspend();
        }

        await db.Tenants.InsertOneAsync(shop, cancellationToken: Token);
        return shop;
    }

    private static async Task<Batch> SeedBatchAsync(
        MongoStorageContext db,
        Tenant shop,
        int quantity,
        long unitCostCents,
        DateOnly expiry)
    {
        var batch = Batch.Receive(shop.Id, Guid.CreateVersion7(), quantity, Money.FromCents(unitCostCents), expiry, Instant.AddDays(-30));
        var changes = new Storage.Application.Abstractions.StockChanges();
        changes.Add(batch);
        await new MongoStockStore(db, new FixedTenant(shop.Id)).CommitAsync(changes, Token);
        return batch;
    }

    private static async Task<Batch> FindBatchAsync(MongoStorageContext db, Guid id) =>
        await db.Batches.Find(Builders<Batch>.Filter.Eq(batch => batch.Id, id)).SingleAsync(Token);
}
