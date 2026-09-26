using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Application.Stock;
using Storage.Application.Tests.Fakes;
using Storage.Domain.Catalog;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Tests.Stock;

public sealed class StockQueriesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Shop = Guid.CreateVersion7();

    private readonly InMemoryStockStore _stock = new(Shop);
    private readonly InMemoryProductRepository _products;
    private readonly InMemoryCategoryRepository _categories;
    private readonly StockQueries _queries;
    private readonly Category _beverages;
    private readonly Category _energy;

    public StockQueriesTests()
    {
        var tenant = new FixedTenant(Shop);
        _categories = new InMemoryCategoryRepository(tenant);
        _products = new InMemoryProductRepository(tenant, _categories);
        _queries = new StockQueries(_stock, _products, _categories);

        _beverages = Category.CreateRoot(Shop, "Bebidas");
        _energy = _beverages.CreateChild("Energéticos");
        _categories.Seed(_beverages);
        _categories.Seed(_energy);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_category_lists_its_products_with_their_stock()
    {
        var drink = Add("Energético 473ml", "7891000000014", _energy, minimum: 0);
        _stock.Seed(drink.Id, 10, 500, new DateOnly(2026, 12, 1), Now);
        _stock.Seed(drink.Id, 30, 600, new DateOnly(2026, 10, 15), Now);

        var item = Assert.Single((await _queries.ListByCategoryAsync(_energy.Id, includeDescendants: false, PageRequest.First(), Token)).Items);

        Assert.Equal(40, item.Quantity);
        Assert.Equal(23000, item.StockValueCents);
        Assert.Equal(575, item.AverageCostCents);
        Assert.Equal(new DateOnly(2026, 10, 15), item.NextExpiry);
        Assert.False(item.BelowMinimum);
    }

    [Fact]
    public async Task A_product_never_received_shows_an_empty_shelf()
    {
        Add("Energético 473ml", "7891000000014", _energy, minimum: 0);

        var item = Assert.Single((await _queries.ListByCategoryAsync(_energy.Id, includeDescendants: false, PageRequest.First(), Token)).Items);

        Assert.Equal(0, item.Quantity);
        Assert.Null(item.NextExpiry);
    }

    [Fact]
    public async Task Below_minimum_lists_the_emptiest_first_and_ignores_unwatched_products()
    {
        var almostOut = Add("Energético 473ml", "7891000000014", _energy, minimum: 10);
        var low = Add("Refrigerante 2L", "7891000000021", _beverages, minimum: 10);
        var fine = Add("Água 500ml", "7891000000038", _beverages, minimum: 5);
        var unwatched = Add("Suco 1L", "7891000000045", _beverages, minimum: 0);

        _stock.Seed(almostOut.Id, 1, 500, null, Now);
        _stock.Seed(low.Id, 6, 500, null, Now);
        _stock.Seed(fine.Id, 20, 100, null, Now);

        var running = (await _queries.ListBelowMinimumAsync(PageRequest.First(), Token)).Items;

        Assert.Equal(["Energético 473ml", "Refrigerante 2L"], running.Select(item => item.Name));
        Assert.DoesNotContain(running, item => item.ProductId == unwatched.Id);
    }

    [Fact]
    public async Task Below_minimum_is_ordered_before_it_is_paged()
    {
        // The order depends on balances, not names: paging first would put the emptiest
        // product on whatever page its name fell on.
        var zebra = Add("Zebra 1L", "7891000000014", _beverages, minimum: 10);
        var abacaxi = Add("Abacaxi 1L", "7891000000021", _beverages, minimum: 10);
        var banana = Add("Banana 1L", "7891000000038", _beverages, minimum: 10);
        _stock.Seed(zebra.Id, 1, 100, null, Now);
        _stock.Seed(abacaxi.Id, 8, 100, null, Now);
        _stock.Seed(banana.Id, 5, 100, null, Now);

        var first = await _queries.ListBelowMinimumAsync(PageRequest.Of(1, 2), Token);
        var second = await _queries.ListBelowMinimumAsync(PageRequest.Of(2, 2), Token);

        Assert.Equal(["Zebra 1L", "Banana 1L"], first.Items.Select(item => item.Name));
        Assert.Equal(["Abacaxi 1L"], second.Items.Select(item => item.Name));
        Assert.Equal(3, second.Total);
    }

    [Fact]
    public async Task The_whole_movement_history_is_reachable_a_page_at_a_time()
    {
        var drink = Add("Energético 473ml", "7891000000014", _energy, minimum: 0);
        for (var day = 0; day < 60; day++)
        {
            var batch = _stock.Seed(drink.Id, 1, 500, null, Now.AddDays(day));
            _stock.Movements.Add(StockMovement.Receipt(batch, Now.AddDays(day), userId: null, receiptId: null));
        }

        var last = await _queries.ListMovementsAsync(drink.Id, PageRequest.Of(3, 25), Token);

        // The product view shows the latest fifty; the oldest ten are here.
        Assert.Equal(10, last.Items.Count);
        Assert.Equal(Now, last.Items[^1].OccurredAt);
        Assert.Equal(60, last.Total);
    }

    [Fact]
    public async Task A_deactivated_product_does_not_ask_to_be_reordered()
    {
        var drink = Add("Energético 473ml", "7891000000014", _energy, minimum: 10);
        drink.Deactivate();

        Assert.Empty((await _queries.ListBelowMinimumAsync(PageRequest.First(), Token)).Items);
    }

    [Fact]
    public async Task A_products_batches_come_in_the_order_they_will_leave()
    {
        var drink = Add("Energético 473ml", "7891000000014", _energy, minimum: 0);
        _stock.Seed(drink.Id, 10, 500, new DateOnly(2026, 12, 1), Now);
        _stock.Seed(drink.Id, 10, 500, new DateOnly(2026, 10, 15), Now);

        var detail = await _queries.GetProductAsync(drink.Id, Token);

        Assert.Equal(
            [new DateOnly(2026, 10, 15), new DateOnly(2026, 12, 1)],
            detail.Batches.Select(batch => batch.ExpiryDate!.Value));
        Assert.Equal(20, detail.Stock.Quantity);
    }

    [Fact]
    public async Task The_summary_adds_up_the_whole_shop()
    {
        var drink = Add("Energético 473ml", "7891000000014", _energy, minimum: 50);
        var water = Add("Água 500ml", "7891000000021", _beverages, minimum: 0);
        _stock.Seed(drink.Id, 10, 500, null, Now);
        _stock.Seed(water.Id, 20, 100, null, Now);

        var summary = await _queries.SummaryAsync(Token);

        Assert.Equal(2, summary.ProductsInStock);
        Assert.Equal(30, summary.Units);
        Assert.Equal(7000, summary.StockValueCents);
        Assert.Equal(1, summary.BelowMinimumCount);
    }

    [Fact]
    public async Task A_category_that_does_not_exist_is_not_found()
    {
        await Refused.WithAsync(
            ErrorKind.NotFound,
            ErrorCodes.CategoryNotFound,
            () => _queries.ListByCategoryAsync(Guid.CreateVersion7(), true, PageRequest.First(), Token));
    }

    private Product Add(string name, string barcode, Category category, int minimum)
    {
        var product = Product.Create(Shop, name, category.Id, UnitOfMeasure.Unit, Money.FromCents(100), Gtin.Parse(barcode));
        product.SetMinimumStock(minimum);
        _products.AddAsync(product).GetAwaiter().GetResult();
        return product;
    }
}
