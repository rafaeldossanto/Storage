using Storage.Application.Errors;
using Storage.Application.Pricing;
using Storage.Application.Tests.Fakes;
using Storage.Domain.Catalog;
using Storage.Domain.Pricing;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Tests.Pricing;

public sealed class PricingServiceTests
{
    private static readonly Guid Shop = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 26);

    private readonly InMemoryStockStore _stock = new(Shop);
    private readonly InMemoryProductRepository _products;
    private readonly InMemoryCategoryRepository _categories;
    private readonly PricingService _service;
    private readonly Category _beverages;
    private readonly Category _energy;
    private readonly Category _sodas;
    private readonly Product _drink;

    public PricingServiceTests()
    {
        var tenant = new FixedTenant(Shop);
        _categories = new InMemoryCategoryRepository(tenant);
        _products = new InMemoryProductRepository(tenant, _categories);
        _service = new PricingService(
            new InMemoryDiscountRuleRepository(tenant), _products, _categories, _stock, new FixedCalendar(Today), tenant);

        _beverages = Category.CreateRoot(Shop, "Bebidas");
        _energy = _beverages.CreateChild("Energéticos");
        _sodas = _beverages.CreateChild("Refrigerantes");
        foreach (var category in new[] { _beverages, _energy, _sodas })
        {
            _categories.Seed(category);
        }

        _drink = Add("Energético 473ml", "7891000000014", _energy);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_saved_rule_changes_the_price_of_what_it_reaches()
    {
        await _service.CreateAsync(Rule("Energéticos -30%", 3000, _energy.Id), Token);

        var quote = await _service.QuoteAsync(_drink.Id, Token);

        Assert.Equal(1000, quote.RegularPriceCents);
        Assert.Equal(700, quote.FinalPriceCents);
        Assert.Equal("Energéticos -30%", Assert.Single(quote.Applied).RuleName);
    }

    [Fact]
    public async Task A_switched_off_rule_stops_changing_prices()
    {
        var rule = await _service.CreateAsync(Rule("Energéticos -30%", 3000, _energy.Id), Token);

        await _service.DeactivateAsync(rule.Id, Token);

        Assert.Equal(1000, (await _service.QuoteAsync(_drink.Id, Token)).FinalPriceCents);
    }

    [Fact]
    public async Task The_price_follows_the_batch_that_leaves_next()
    {
        await _service.CreateAsync(Rule("Vence em 3 dias -30%", 3000, _energy.Id) with { ExpiringWithinDays = 3 }, Token);
        _stock.Seed(_drink.Id, 10, 500, Today.AddDays(30), Now);
        _stock.Seed(_drink.Id, 2, 500, Today.AddDays(2), Now);

        var quote = await _service.QuoteAsync(_drink.Id, Token);

        // Two cans expire in two days and go out first, so they are what gets priced.
        Assert.Equal(700, quote.FinalPriceCents);
        Assert.Equal(Today.AddDays(2), quote.PricedBatchExpiry);
    }

    [Fact]
    public async Task Once_the_expiring_batch_is_gone_the_regular_price_is_back()
    {
        await _service.CreateAsync(Rule("Vence em 3 dias -30%", 3000, _energy.Id) with { ExpiringWithinDays = 3 }, Token);
        _stock.Seed(_drink.Id, 10, 500, Today.AddDays(30), Now);

        Assert.Equal(1000, (await _service.QuoteAsync(_drink.Id, Token)).FinalPriceCents);
    }

    [Fact]
    public async Task The_preview_counts_every_product_in_the_branch()
    {
        Add("Energético Zero", "7891000000021", _energy);
        Add("Refrigerante 2L", "7891000000038", _sodas);

        var onBeverages = await _service.PreviewAsync(DiscountTarget.Category, _beverages.Id, Token);
        var onEnergy = await _service.PreviewAsync(DiscountTarget.Category, _energy.Id, Token);

        Assert.Equal(3, onBeverages.ProductCount);
        Assert.Equal(2, onEnergy.ProductCount);
        Assert.DoesNotContain("Refrigerante 2L", onEnergy.SampleNames);
    }

    [Fact]
    public async Task A_rule_aimed_at_something_outside_the_shop_is_refused()
    {
        await Refused.WithAsync(
            ErrorKind.NotFound,
            ErrorCodes.DiscountTargetNotFound,
            () => _service.CreateAsync(Rule("Fantasma", 1000, Guid.CreateVersion7()), Token));
    }

    [Fact]
    public async Task Editing_a_rule_replaces_its_terms()
    {
        var rule = await _service.CreateAsync(Rule("Energéticos -30%", 3000, _energy.Id), Token);

        var edited = await _service.UpdateAsync(rule.Id, Rule("Energéticos -10%", 1000, _energy.Id), Token);

        Assert.Equal("Energéticos -10%", edited.Name);
        Assert.Equal(900, (await _service.QuoteAsync(_drink.Id, Token)).FinalPriceCents);
    }

    private static SaveDiscountRuleRequest Rule(string name, long basisPoints, Guid categoryId) =>
        new(name, DiscountType.Percentage, basisPoints, DiscountTarget.Category, categoryId);

    private Product Add(string name, string barcode, Category category)
    {
        var product = Product.Create(Shop, name, category.Id, UnitOfMeasure.Unit, Money.FromCents(1000), Gtin.Parse(barcode));
        _products.AddAsync(product).GetAwaiter().GetResult();
        return product;
    }
}
