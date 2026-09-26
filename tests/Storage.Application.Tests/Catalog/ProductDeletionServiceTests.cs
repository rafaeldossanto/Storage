using Storage.Application.Catalog;
using Storage.Application.Errors;
using Storage.Application.Tests.Fakes;
using Storage.Domain.Catalog;
using Storage.Domain.Pricing;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Tests.Catalog;

public sealed class ProductDeletionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Shop = Guid.CreateVersion7();

    private readonly InMemoryProductRepository _products;
    private readonly InMemoryStockStore _stock = new(Shop);
    private readonly InMemoryDiscountRuleRepository _discounts;
    private readonly InMemoryCountStore _counts;
    private readonly ProductDeletionService _service;
    private readonly Product _drink;

    public ProductDeletionServiceTests()
    {
        var tenant = new FixedTenant(Shop);
        _products = new InMemoryProductRepository(tenant);
        _discounts = new InMemoryDiscountRuleRepository(tenant);
        _counts = new InMemoryCountStore(tenant);
        _service = new ProductDeletionService(_products, _stock, _discounts, _counts);

        _drink = Product.Create(Shop, "Energético 473ml", Guid.CreateVersion7(), UnitOfMeasure.Unit, Money.FromCents(899), Gtin.Parse("7891000000014"));
        _products.AddAsync(_drink).GetAwaiter().GetResult();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_product_registered_by_mistake_is_gone_and_its_barcode_free_again()
    {
        await _service.DeleteAsync(_drink.Id, Token);

        Assert.Null(await _products.FindByGtinAsync(Gtin.Parse("7891000000014"), Token));
    }

    [Fact]
    public async Task A_product_with_stock_history_is_kept()
    {
        var batch = _stock.Seed(_drink.Id, 12, 500, null, Now);
        _stock.Movements.Add(StockMovement.Receipt(batch, Now, userId: null, receiptId: null));

        await Refused.WithAsync(ErrorKind.Conflict, ErrorCodes.ProductInUse, () => _service.DeleteAsync(_drink.Id, Token));

        Assert.NotNull(await _products.FindAsync(_drink.Id, Token));
    }

    [Fact]
    public async Task A_product_a_discount_is_aimed_at_is_kept()
    {
        await _discounts.AddAsync(
            DiscountRule.Create(Shop, "Queima", DiscountType.Percentage, 1_000, DiscountTarget.Product, _drink.Id), Token);

        await Refused.WithAsync(ErrorKind.Conflict, ErrorCodes.ProductInUse, () => _service.DeleteAsync(_drink.Id, Token));
    }

    [Fact]
    public async Task A_product_that_was_counted_is_kept()
    {
        var count = StockCount.Start(Shop, null, Guid.CreateVersion7(), Now);
        count.SetCounted(_drink.Id, 0);
        _counts.Stored.Add(count);

        await Refused.WithAsync(ErrorKind.Conflict, ErrorCodes.ProductInUse, () => _service.DeleteAsync(_drink.Id, Token));
    }

    [Fact]
    public async Task An_unknown_product_is_not_found()
    {
        await Refused.WithAsync(
            ErrorKind.NotFound, ErrorCodes.ProductNotFound, () => _service.DeleteAsync(Guid.CreateVersion7(), Token));
    }
}
