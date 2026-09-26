using MongoDB.Bson;
using MongoDB.Driver;
using Storage.Application.Errors;
using Storage.Domain.Accounts;
using Storage.Domain.Catalog;
using Storage.Infrastructure.Persistence;

namespace Storage.Integration.Tests.Mongo;

public sealed class AccountStoreTests(MongoFixture mongo)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_e_mail_is_unique_across_every_shop()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        await ProvisionAsync(store, "Mercadinho A", "dono@loja.com");

        var clash = await Assert.ThrowsAsync<UseCaseException>(
            () => ProvisionAsync(store, "Mercadinho B", "DONO@loja.com"));

        Assert.Equal(ErrorCodes.EmailTaken, clash.Code);
    }

    [Fact]
    public async Task Losing_the_e_mail_race_leaves_nothing_behind()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        await ProvisionAsync(store, "Mercadinho A", "dono@loja.com");

        await Assert.ThrowsAsync<UseCaseException>(() => ProvisionAsync(store, "Mercadinho B", "dono@loja.com"));

        // The owner is written first precisely so the loser fails before its shop and its
        // categories exist.
        Assert.Equal(1, await db.Tenants.CountDocumentsAsync(FilterDefinition<Tenant>.Empty, cancellationToken: Token));
        Assert.Equal(1, await db.Categories.CountDocumentsAsync(FilterDefinition<Category>.Empty, cancellationToken: Token));
    }

    [Fact]
    public async Task Expired_sessions_are_left_to_a_TTL_index()
    {
        var db = await mongo.NewDatabaseAsync(Token);

        var indexes = await (await db.Sessions.Indexes.ListAsync(Token)).ToListAsync(Token);
        var ttl = Assert.Single(indexes, index => index.Contains("expireAfterSeconds"));

        Assert.Equal(0, ttl["expireAfterSeconds"].ToInt32());
        Assert.Equal(new BsonDocument("ExpiresAt", 1), ttl["key"].AsBsonDocument);
    }

    [Fact]
    public async Task Two_wrong_pins_typed_at_once_both_count()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var (shop, _) = await ProvisionAsync(store, "Mercadinho A", "dono@loja.com");

        // Two attempts read the same state before either writes.
        var first = (await store.FindTenantAsync(shop.Id, Token))!;
        var second = (await store.FindTenantAsync(shop.Id, Token))!;
        first.RecordSalesPinFailure(Now);
        second.RecordSalesPinFailure(Now);

        Assert.True(await store.SaveSalesPinAsync(first, expectedVersion: 0, Token));
        Assert.False(await store.SaveSalesPinAsync(second, expectedVersion: 0, Token));

        // The loser reads again and counts on top of the winner.
        var again = (await store.FindTenantAsync(shop.Id, Token))!;
        again.RecordSalesPinFailure(Now);
        Assert.True(await store.SaveSalesPinAsync(again, expectedVersion: 1, Token));
        Assert.Equal(2, (await store.FindTenantAsync(shop.Id, Token))!.SalesPinFailures);
    }

    [Fact]
    public async Task A_shop_from_before_the_pin_existed_can_get_one()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var (shop, _) = await ProvisionAsync(store, "Mercadinho A", "dono@loja.com");
        await db.Tenants.UpdateOneAsync(
            Builders<Tenant>.Filter.Eq(stored => stored.Id, shop.Id),
            Builders<Tenant>.Update.Unset(stored => stored.SalesPinVersion),
            cancellationToken: Token);

        var old = (await store.FindTenantAsync(shop.Id, Token))!;
        old.SetSalesPin("hash");

        Assert.True(await store.SaveSalesPinAsync(old, expectedVersion: 0, Token));
        Assert.True((await store.FindTenantAsync(shop.Id, Token))!.HasSalesPin);
    }

    [Fact]
    public async Task Of_two_simultaneous_rotations_of_one_session_only_one_wins()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var ana = User.CreateStaff(Guid.CreateVersion7(), "Ana", EmailAddress.Parse("ana@loja.com"), "hash");
        await store.AddSessionAsync(Session.Start(ana, "celular", Now, TimeSpan.FromDays(30)), Token);

        // Two refreshes read the same active session before either writes.
        var first = (await store.FindSessionByTokenHashAsync("celular", Token))!;
        var second = (await store.FindSessionByTokenHashAsync("celular", Token))!;
        var fromFirst = first.Rotate("depois-1", Now, TimeSpan.FromDays(30));
        var fromSecond = second.Rotate("depois-2", Now, TimeSpan.FromDays(30));

        var outcomes = await Task.WhenAll(
            store.RotateSessionAsync(first, fromFirst, Token),
            store.RotateSessionAsync(second, fromSecond, Token));

        Assert.Single(outcomes, won => won);
        Assert.Equal(2, await db.Sessions.CountDocumentsAsync(FilterDefinition<Session>.Empty, cancellationToken: Token));
    }

    [Fact]
    public async Task Revoking_everything_ends_only_that_persons_open_sessions()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var ana = User.CreateStaff(Guid.CreateVersion7(), "Ana", EmailAddress.Parse("ana@loja.com"), "hash");
        var bia = User.CreateStaff(ana.TenantId, "Bia", EmailAddress.Parse("bia@loja.com"), "hash");
        var closedEarlier = Session.Start(ana, "antiga", Now, TimeSpan.FromDays(30));
        closedEarlier.Revoke(Now);

        await store.AddSessionAsync(Session.Start(ana, "celular", Now, TimeSpan.FromDays(30)), Token);
        await store.AddSessionAsync(Session.Start(ana, "computador", Now, TimeSpan.FromDays(30)), Token);
        await store.AddSessionAsync(closedEarlier, Token);
        await store.AddSessionAsync(Session.Start(bia, "caixa", Now, TimeSpan.FromDays(30)), Token);

        await store.RevokeAllSessionsAsync(ana.Id, Now.AddHours(1), Token);

        Assert.True((await store.FindSessionByTokenHashAsync("celular", Token))!.IsRevoked);
        Assert.True((await store.FindSessionByTokenHashAsync("computador", Token))!.IsRevoked);
        Assert.Equal(Now, (await store.FindSessionByTokenHashAsync("antiga", Token))!.RevokedAt);
        Assert.False((await store.FindSessionByTokenHashAsync("caixa", Token))!.IsRevoked);
    }

    [Fact]
    public async Task A_signed_up_shop_can_be_read_back_with_its_owner()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var store = Store(db);
        var (tenant, owner) = await ProvisionAsync(store, "Mercadinho A", "dono@loja.com");

        var byEmail = await store.FindUserByEmailAsync(EmailAddress.Parse("Dono@Loja.com"), Token);
        var shop = await store.FindTenantAsync(tenant.Id, Token);

        Assert.Equal(owner.Id, byEmail?.Id);
        Assert.Equal(UserRole.Owner, byEmail?.Role);
        Assert.Equal("America/Sao_Paulo", shop?.TimeZoneId);
        Assert.Equal(Now, shop?.CreatedAt);
    }

    private static MongoAccountStore Store(MongoStorageContext db) => new(db, new FixedClock(Now));

    private static async Task<(Tenant, User)> ProvisionAsync(MongoAccountStore store, string shop, string email)
    {
        var tenant = Tenant.Create(shop);
        var owner = User.CreateOwner(tenant.Id, "Dono", EmailAddress.Parse(email), "hash");
        await store.ProvisionAsync(tenant, owner, [Category.CreateRoot(tenant.Id, "Bebidas")], Token);
        return (tenant, owner);
    }
}
