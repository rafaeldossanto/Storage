using Storage.Application.Abstractions;
using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;
using Storage.Infrastructure.Persistence;

namespace Storage.Integration.Tests.Mongo;

/// <summary>
/// The photo queue and the pictures against a real MongoDB: the upsert that must not reset
/// a known code, the claim that must hand a code to one worker only, and the scan across
/// every shop's catalogue.
/// </summary>
public sealed class ProductPhotoStoreTests(MongoFixture mongo)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Gtin Coke = Gtin.Parse("7894900010015");
    private static readonly Gtin RedBull = Gtin.Parse("9002490100070");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stored_photo_comes_back_with_its_picture()
    {
        var store = await NewStoreAsync();
        await store.RequestAsync([Coke], Token);
        var photo = (await store.ClaimNextAsync(Now, TimeSpan.FromMinutes(5), Token))!;

        photo.Store("v1", "Open Food Facts", "https://world.openfoodfacts.org/product/7894900010015", "CC BY-SA 3.0", Now);
        await store.SaveAsync(photo, new PhotoFile("v1", "image/webp", [1, 2, 3]), Token);

        var ready = await store.FindReadyAsync([Coke, RedBull], Token);
        Assert.Equal("v1", ready[Coke].Version);
        Assert.Equal("Open Food Facts", ready[Coke].Source);
        Assert.False(ready.ContainsKey(RedBull));

        var file = await store.OpenAsync(Coke, Token);
        Assert.Equal([1, 2, 3], file!.Content);
        Assert.Equal("image/webp", file.ContentType);
    }

    [Fact]
    public async Task Asking_again_for_a_known_code_leaves_it_as_it_is()
    {
        var store = await NewStoreAsync();
        await store.RequestAsync([Coke], Token);
        var photo = (await store.ClaimNextAsync(Now, TimeSpan.FromMinutes(5), Token))!;
        photo.Store("v1", "Open Food Facts", "https://example.org", "CC BY-SA 3.0", Now);
        await store.SaveAsync(photo, new PhotoFile("v1", "image/webp", [1]), Token);

        await store.RequestAsync([Coke, Coke], Token);

        Assert.Equal("v1", (await store.FindReadyAsync([Coke], Token))[Coke].Version);
    }

    [Fact]
    public async Task A_claimed_code_goes_to_one_worker_until_its_lease_runs_out()
    {
        var store = await NewStoreAsync();
        await store.RequestAsync([Coke], Token);

        var first = await store.ClaimNextAsync(Now, TimeSpan.FromMinutes(5), Token);
        var second = await store.ClaimNextAsync(Now.AddMinutes(1), TimeSpan.FromMinutes(5), Token);
        var afterLease = await store.ClaimNextAsync(Now.AddMinutes(5), TimeSpan.FromMinutes(5), Token);

        Assert.Equal(Coke, first!.Gtin);
        Assert.Null(second);
        Assert.Equal(Coke, afterLease!.Gtin);
    }

    [Fact]
    public async Task Two_workers_claiming_at_once_never_get_the_same_code()
    {
        var store = await NewStoreAsync();
        await store.RequestAsync([Coke, RedBull], Token);

        var claims = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => store.ClaimNextAsync(Now, TimeSpan.FromMinutes(5), Token)));

        var claimed = claims.OfType<ProductPhoto>().Select(photo => photo.Gtin).ToArray();
        Assert.Equal(2, claimed.Length);
        Assert.Equal(2, claimed.Distinct().Count());
    }

    [Fact]
    public async Task A_code_waiting_for_a_retry_is_not_claimed_before_its_time()
    {
        var store = await NewStoreAsync();
        await store.RequestAsync([Coke], Token);
        var photo = (await store.ClaimNextAsync(Now, TimeSpan.FromMinutes(5), Token))!;

        photo.MarkMissing(Now);
        await store.SaveAsync(photo, file: null, Token);

        Assert.Null(await store.ClaimNextAsync(Now.AddDays(29), TimeSpan.FromMinutes(5), Token));
        Assert.NotNull(await store.ClaimNextAsync(Now.AddDays(30), TimeSpan.FromMinutes(5), Token));
    }

    [Fact]
    public async Task Every_shops_codes_are_asked_for_once_but_never_the_ones_a_shop_minted()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = new MongoProductPhotoStore(db, new FixedClock(Now));
        var shopA = Guid.CreateVersion7();
        var shopB = Guid.CreateVersion7();
        var weighed = Gtin.Parse("200000100000" + Gtin.CalculateCheckDigit("200000100000"));

        var cokeWithPack = NewProduct(shopA, Coke);
        cokeWithPack.AddPackaging(Gtin.Parse("17894900010012"), "Fardo 12", 12);
        await Products(db, shopA).AddAsync(cokeWithPack, Token);
        await Products(db, shopA).AddAsync(NewProduct(shopA, weighed), Token);
        await Products(db, shopB).AddAsync(NewProduct(shopB, Coke), Token);
        await Products(db, shopB).AddAsync(NewProduct(shopB, RedBull), Token);

        // Coke is already known: its entry must survive the scan untouched.
        await store.RequestAsync([Coke], Token);
        var claimedCoke = (await store.ClaimNextAsync(Now, TimeSpan.FromDays(365), Token))!;
        Assert.Equal(Coke, claimedCoke.Gtin);

        await store.RequestAllCatalogedAsync(Token);

        var due = await ClaimAllDueAsync(store);

        Assert.Equal(2, due.Count);
        Assert.Contains(RedBull, due);
        Assert.Contains(Gtin.Parse("17894900010012"), due);
    }

    /// <summary>
    /// Claims until nothing is due. Bounded: a claim that stopped pushing the next attempt
    /// ahead would hand out the same code forever, and must fail the test, not hang it.
    /// </summary>
    private static async Task<List<Gtin>> ClaimAllDueAsync(MongoProductPhotoStore store)
    {
        var due = new List<Gtin>();

        for (var claims = 0; claims < 10; claims++)
        {
            var photo = await store.ClaimNextAsync(Now, TimeSpan.FromMinutes(5), Token);

            if (photo is null)
            {
                return due;
            }

            due.Add(photo.Gtin);
        }

        return due;
    }

    private async Task<MongoProductPhotoStore> NewStoreAsync() =>
        new(await mongo.NewDatabaseAsync(Token), new FixedClock(Now));

    private static ProductRepository Products(MongoStorageContext db, Guid shop) =>
        new(db, new FixedTenant(shop), new FixedClock(Now));

    private static Product NewProduct(Guid shop, Gtin gtin) =>
        Product.Create(shop, "Produto", Guid.CreateVersion7(), UnitOfMeasure.Unit, Money.FromCents(899), gtin);
}
