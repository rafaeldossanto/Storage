using Storage.Application.Errors;
using Storage.Application.Sales;
using Storage.Application.Stock;
using Storage.Application.Tests.Fakes;
using Storage.Domain.Catalog;
using Storage.Domain.Common;
using Storage.Domain.Pricing;
using Storage.Domain.Sales;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Tests.Sales;

public sealed class SalesServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 26);
    private static readonly Guid Shop = Guid.CreateVersion7();
    private static readonly Guid Cashier = Guid.CreateVersion7();

    private readonly ManualClock _clock = new(Now);
    private readonly InMemoryStockStore _stock = new(Shop);
    private readonly InMemoryDiscountRuleRepository _discounts;
    private readonly SalesService _service;
    private readonly Category _energy;
    private readonly Product _drink;
    private readonly Product _soap;

    public SalesServiceTests()
    {
        var tenant = new FixedTenant(Shop);
        var categories = new InMemoryCategoryRepository(tenant);
        var products = new InMemoryProductRepository(tenant, categories);
        _discounts = new InMemoryDiscountRuleRepository(tenant);
        _service = new SalesService(
            _stock,
            new InMemorySaleStore(_stock, tenant),
            products,
            categories,
            _discounts,
            new FixedCalendar(Today),
            tenant,
            new FixedUser(Cashier),
            _clock);

        _energy = Category.CreateRoot(Shop, "Energéticos");
        var cleaning = Category.CreateRoot(Shop, "Limpeza");
        categories.Seed(_energy);
        categories.Seed(cleaning);

        _drink = Product.Create(Shop, "Energético 473ml", _energy.Id, UnitOfMeasure.Unit, Money.FromCents(899), Gtin.Parse("7891000000014"));
        _soap = Product.Create(Shop, "Sabão em barra", cleaning.Id, UnitOfMeasure.Unit, Money.FromCents(450), Gtin.Parse("7891000000021"));
        products.AddAsync(_drink).GetAwaiter().GetResult();
        products.AddAsync(_soap).GetAwaiter().GetResult();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static RegisterSaleRequest Selling(params (Product Product, int Quantity)[] items) =>
        new(items.Select(item => new SaleItemRequest(item.Product.Id, item.Quantity)).ToArray());

    [Fact]
    public async Task A_sale_takes_the_batches_that_expire_first_and_costs_what_they_cost()
    {
        var soon = _stock.Seed(_drink.Id, 2, 400, new DateOnly(2026, 10, 1), Now);
        var later = _stock.Seed(_drink.Id, 10, 500, new DateOnly(2026, 12, 1), Now);

        var sale = await _service.RegisterAsync(Selling((_drink, 3)), Token);

        Assert.Equal(0, soon.RemainingQuantity);
        Assert.Equal(9, later.RemainingQuantity);
        Assert.Equal(3 * 899, sale.TotalCents);

        var recorded = Assert.Single(_stock.Documents.OfType<Sale>());
        Assert.Equal(2 * 400 + 500, recorded.Cost.Cents);
        Assert.All(_stock.Movements, movement =>
        {
            Assert.Equal(MovementType.Sale, movement.Type);
            Assert.Equal(sale.Id, movement.DocumentId);
            Assert.Equal(Cashier, movement.UserId);
        });
        Assert.Equal(1, _stock.Commits);
    }

    [Fact]
    public async Task The_price_charged_is_the_one_the_discount_rules_give()
    {
        _stock.Seed(_drink.Id, 10, 500, null, Now);
        await _discounts.AddAsync(
            DiscountRule.Create(Shop, "Energéticos -10%", DiscountType.Percentage, 1_000, DiscountTarget.Category, _energy.Id), Token);

        var sale = await _service.RegisterAsync(Selling((_drink, 1)), Token);

        Assert.Equal(809, sale.Lines[0].UnitPriceCents);
    }

    [Fact]
    public async Task The_same_product_scanned_twice_is_one_line()
    {
        _stock.Seed(_drink.Id, 20, 500, null, Now);

        var sale = await _service.RegisterAsync(Selling((_drink, 1), (_drink, 12)), Token);

        var line = Assert.Single(sale.Lines);
        Assert.Equal(13, line.Quantity);
    }

    [Fact]
    public async Task Selling_more_than_the_shelf_holds_is_refused_naming_the_line_and_changes_nothing()
    {
        _stock.Seed(_drink.Id, 10, 500, null, Now);
        var soap = _stock.Seed(_soap.Id, 1, 300, null, Now);

        var refusal = await Assert.ThrowsAsync<DomainException>(
            () => _service.RegisterAsync(Selling((_drink, 1), (_soap, 2)), Token));

        Assert.Equal(DomainErrors.StockInsufficient, refusal.Code);
        Assert.Equal(2, refusal.Data[ReceivingService.LineKey]);
        Assert.Equal(0, _stock.Commits);
        Assert.Equal(1, soap.RemainingQuantity);
    }

    [Fact]
    public async Task An_empty_sale_is_refused()
    {
        var refusal = await Assert.ThrowsAsync<DomainException>(() => _service.RegisterAsync(new RegisterSaleRequest([]), Token));

        Assert.Equal(DomainErrors.SaleEmpty, refusal.Code);
    }

    [Fact]
    public async Task Cancelling_puts_the_units_back_where_they_came_from()
    {
        var soon = _stock.Seed(_drink.Id, 2, 400, new DateOnly(2026, 10, 1), Now);
        var later = _stock.Seed(_drink.Id, 10, 500, new DateOnly(2026, 12, 1), Now);
        var sale = await _service.RegisterAsync(Selling((_drink, 3)), Token);
        _clock.Advance(TimeSpan.FromMinutes(2));

        var cancelled = await _service.CancelAsync(sale.Id, Token);

        Assert.Equal(SaleStatus.Cancelled, cancelled.Status);
        Assert.Equal(2, soon.RemainingQuantity);
        Assert.Equal(BatchStatus.Available, soon.Status);
        Assert.Equal(10, later.RemainingQuantity);
        Assert.Equal(3, _stock.Movements.Where(movement => movement.Type == MovementType.SaleCancellation).Sum(movement => movement.Quantity));
    }

    [Fact]
    public async Task A_sale_cannot_be_cancelled_twice()
    {
        _stock.Seed(_drink.Id, 10, 500, null, Now);
        var sale = await _service.RegisterAsync(Selling((_drink, 1)), Token);
        await _service.CancelAsync(sale.Id, Token);

        var refusal = await Assert.ThrowsAsync<DomainException>(() => _service.CancelAsync(sale.Id, Token));

        Assert.Equal(DomainErrors.SaleNotCompleted, refusal.Code);
    }

    [Fact]
    public async Task An_old_sale_stays_on_the_books()
    {
        _stock.Seed(_drink.Id, 10, 500, null, Now);
        var sale = await _service.RegisterAsync(Selling((_drink, 1)), Token);
        _clock.Advance(Sale.CancellationWindow + TimeSpan.FromSeconds(1));

        var refusal = await Assert.ThrowsAsync<DomainException>(() => _service.CancelAsync(sale.Id, Token));

        Assert.Equal(DomainErrors.SaleCancelWindowClosed, refusal.Code);
    }

    [Fact]
    public async Task An_unknown_sale_is_not_found()
    {
        await Refused.WithAsync(ErrorKind.NotFound, ErrorCodes.SaleNotFound, () => _service.CancelAsync(Guid.CreateVersion7(), Token));
    }
}
