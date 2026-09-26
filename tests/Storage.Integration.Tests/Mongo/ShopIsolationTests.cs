using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;
using Storage.Infrastructure.Persistence;

namespace Storage.Integration.Tests.Mongo;

/// <summary>
/// The promise a multi-tenant SaaS lives or dies by: one shop never sees another's data.
/// Checked against a real MongoDB, because the filters and the unique indexes only mean
/// something once the database is the one applying them.
/// </summary>
public sealed class ShopIsolationTests(MongoFixture mongo)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ShopA = Guid.CreateVersion7();
    private static readonly Guid ShopB = Guid.CreateVersion7();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_category_of_another_shop_is_invisible()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var beverages = Category.CreateRoot(ShopA, "Bebidas");
        await Categories(db, ShopA).AddAsync(beverages, Token);

        var fromB = Categories(db, ShopB);

        Assert.Null(await fromB.FindAsync(beverages.Id, Token));
        Assert.Empty(await fromB.ListAsync(Token));
        Assert.Single(await Categories(db, ShopA).ListAsync(Token));
    }

    [Fact]
    public async Task A_product_of_another_shop_is_invisible_by_id_by_barcode_and_by_name()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var product = NewProduct(ShopA, "7891000000014", "Energético 473ml");
        await Products(db, ShopA).AddAsync(product, Token);

        var fromB = Products(db, ShopB);

        Assert.Null(await fromB.FindAsync(product.Id, Token));
        Assert.Null(await fromB.FindByGtinAsync(Gtin.Parse("7891000000014"), Token));
        Assert.Empty((await fromB.SearchAsync("Energético", PageRequest.First(), Token)).Items);
    }

    [Fact]
    public async Task Two_shops_may_sell_the_same_barcode()
    {
        var db = await mongo.NewDatabaseAsync(Token);

        await Products(db, ShopA).AddAsync(NewProduct(ShopA, "7891000000014"), Token);
        await Products(db, ShopB).AddAsync(NewProduct(ShopB, "7891000000014"), Token);

        Assert.NotNull(await Products(db, ShopA).FindByGtinAsync(Gtin.Parse("7891000000014"), Token));
        Assert.NotNull(await Products(db, ShopB).FindByGtinAsync(Gtin.Parse("7891000000014"), Token));
    }

    [Fact]
    public async Task The_unique_index_settles_a_barcode_race_inside_one_shop()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var products = Products(db, ShopA);
        await products.AddAsync(NewProduct(ShopA, "7891000000014"), Token);

        // Straight to the repository, skipping the use case's check: this is the request
        // that passed that check at the same instant as the first one.
        var loser = await Assert.ThrowsAsync<UseCaseException>(
            () => products.AddAsync(NewProduct(ShopA, "7891000000014", "Outro"), Token));

        Assert.Equal(ErrorKind.Conflict, loser.Kind);
        Assert.Equal(ErrorCodes.BarcodeTaken, loser.Code);
    }

    [Fact]
    public async Task A_pack_barcode_counts_as_taken_too()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var can = NewProduct(ShopA, "7891000000014");
        can.AddPackaging(Gtin.Parse("17891000000011"), "Fardo 12", 12);
        await Products(db, ShopA).AddAsync(can, Token);

        await Assert.ThrowsAsync<UseCaseException>(
            () => Products(db, ShopA).AddAsync(NewProduct(ShopA, "17891000000011", "Outro"), Token));
    }

    [Fact]
    public async Task Sibling_categories_cannot_share_a_name_but_cousins_can()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var categories = Categories(db, ShopA);
        var beverages = Category.CreateRoot(ShopA, "Bebidas");
        var grocery = Category.CreateRoot(ShopA, "Mercearia");
        await categories.AddAsync(beverages, Token);
        await categories.AddAsync(grocery, Token);
        await categories.AddAsync(beverages.CreateChild("Outros"), Token);

        // Same name under another parent: fine.
        await categories.AddAsync(grocery.CreateChild("Outros"), Token);

        var clash = await Assert.ThrowsAsync<UseCaseException>(
            () => categories.AddAsync(beverages.CreateChild("Outros"), Token));

        Assert.Equal(ErrorCodes.CategoryNameTaken, clash.Code);
    }

    [Fact]
    public async Task A_repository_refuses_to_write_another_shops_document()
    {
        var db = await mongo.NewDatabaseAsync(Token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Categories(db, ShopB).AddAsync(Category.CreateRoot(ShopA, "Bebidas"), Token));
    }

    [Fact]
    public async Task Deleting_a_product_frees_its_barcode_and_cannot_reach_another_shop()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var ours = NewProduct(ShopA, "7891000000014");
        await Products(db, ShopA).AddAsync(ours, Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Products(db, ShopB).DeleteAsync(ours, Token));
        Assert.NotNull(await Products(db, ShopA).FindAsync(ours.Id, Token));

        await Products(db, ShopA).DeleteAsync(ours, Token);

        Assert.Null(await Products(db, ShopA).FindAsync(ours.Id, Token));
        // The unique index lets the same barcode be registered again.
        await Products(db, ShopA).AddAsync(NewProduct(ShopA, "7891000000014"), Token);
    }

    [Fact]
    public async Task Moving_a_branch_persists_every_node_and_the_prefix_query_follows_it()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var categories = Categories(db, ShopA);
        var beverages = Category.CreateRoot(ShopA, "Bebidas");
        var grocery = Category.CreateRoot(ShopA, "Mercearia");
        var energy = beverages.CreateChild("Energéticos");
        var sugarFree = energy.CreateChild("Zero açúcar");
        foreach (var category in new[] { beverages, grocery, energy, sugarFree })
        {
            await categories.AddAsync(category, Token);
        }

        var branch = await categories.ListDescendantsAsync(energy, Token);
        energy.MoveTo(grocery, branch);
        await categories.UpdateManyAsync([energy, .. branch], Token);

        var underGrocery = await categories.ListDescendantsAsync(grocery, Token);
        var underBeverages = await categories.ListDescendantsAsync(beverages, Token);

        Assert.Equal(["Energéticos", "Zero açúcar"], underGrocery.Select(c => c.Name).Order());
        Assert.Empty(underBeverages);
    }

    [Fact]
    public async Task Listing_a_category_can_include_its_whole_branch()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var beverages = Category.CreateRoot(ShopA, "Bebidas");
        var energy = beverages.CreateChild("Energéticos");
        await Categories(db, ShopA).AddAsync(beverages, Token);
        await Categories(db, ShopA).AddAsync(energy, Token);

        var drink = Product.Create(ShopA, "Energético 473ml", energy.Id, UnitOfMeasure.Unit,
            Money.FromCents(899), Gtin.Parse("7891000000014"));
        await Products(db, ShopA).AddAsync(drink, Token);

        Assert.Single((await Products(db, ShopA).ListByCategoryAsync(beverages, includeDescendants: true, PageRequest.First(), Token)).Items);
        Assert.Empty((await Products(db, ShopA).ListByCategoryAsync(beverages, includeDescendants: false, PageRequest.First(), Token)).Items);
    }

    [Fact]
    public async Task Name_search_ignores_case_and_treats_the_text_literally()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        await Products(db, ShopA).AddAsync(NewProduct(ShopA, "7891000000014", "Leite (1L) Integral"), Token);

        // "(1L" is not a valid regular expression; searching for it must not blow up.
        Assert.Single((await Products(db, ShopA).SearchAsync("LEITE", PageRequest.First(), Token)).Items);
        Assert.Single((await Products(db, ShopA).SearchAsync("(1L", PageRequest.First(), Token)).Items);
    }

    [Fact]
    public async Task A_product_comes_back_from_the_database_exactly_as_it_went_in()
    {
        var db = await mongo.NewDatabaseAsync(Token);
        var product = NewProduct(ShopA, "7891000000014");
        product.AddPackaging(Gtin.Parse("17891000000011"), "Fardo 12", 12);
        await Products(db, ShopA).AddAsync(product, Token);

        var stored = await Products(db, ShopA).FindAsync(product.Id, Token);

        Assert.NotNull(stored);
        Assert.Equal(899, stored.SalePrice.Cents);
        Assert.Equal(2, stored.Packagings.Count);
        Assert.Equal(12, stored.BaseUnitsFor(Gtin.Parse("17891000000011")));
        Assert.Equal(Now, stored.CreatedAt);
    }

    private static CategoryRepository Categories(MongoStorageContext db, Guid shop) =>
        new(db, new FixedTenant(shop));

    private static ProductRepository Products(MongoStorageContext db, Guid shop) =>
        new(db, new FixedTenant(shop), new FixedClock(Now));

    private static Product NewProduct(Guid shop, string barcode, string name = "Energético 473ml") =>
        Product.Create(shop, name, Guid.CreateVersion7(), UnitOfMeasure.Unit, Money.FromCents(899), Gtin.Parse(barcode));
}
