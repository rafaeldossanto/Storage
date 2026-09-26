using Storage.Application.Errors;
using Storage.Application.Reports;
using Storage.Application.Tests.Fakes;
using Storage.Domain.Catalog;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Tests.Reports;

public sealed class ReportsServiceTests
{
    private static readonly Guid Shop = Guid.CreateVersion7();
    private static readonly DateOnly Today = new(2026, 9, 26);
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryStockStore _stock = new(Shop);
    private readonly InMemoryProductRepository _products;
    private readonly InMemoryCategoryRepository _categories;
    private readonly ReportsService _reports;
    private readonly Product _drink;
    private readonly Product _soap;

    public ReportsServiceTests()
    {
        var tenant = new FixedTenant(Shop);
        _categories = new InMemoryCategoryRepository(tenant);
        _products = new InMemoryProductRepository(tenant, _categories);
        _reports = new ReportsService(_stock, _products, _categories, new FixedCalendar(Today));

        var beverages = Category.CreateRoot(Shop, "Bebidas");
        var energy = beverages.CreateChild("Energéticos");
        var cleaning = Category.CreateRoot(Shop, "Limpeza");
        foreach (var category in new[] { beverages, energy, cleaning })
        {
            _categories.Seed(category);
        }

        _drink = Add("Energético 473ml", "7891000000014", energy);
        _soap = Add("Sabão em barra", "7891000000021", cleaning);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_dashboard_counts_what_expires_in_each_window()
    {
        _stock.Seed(_drink.Id, 6, 500, Today.AddDays(2), Now);
        _stock.Seed(_drink.Id, 10, 500, Today.AddDays(10), Now);
        _stock.Seed(_soap.Id, 4, 300, Today.AddDays(25), Now);
        _stock.Seed(_soap.Id, 99, 300, Today.AddDays(60), Now);
        _stock.Seed(_soap.Id, 99, 300, expiry: null, Now);

        var dashboard = await _reports.ExpiringAsync(Token);

        Assert.Equal(
            [(3, 6, 3000L), (7, 6, 3000L), (15, 16, 8000L), (30, 20, 9200L)],
            dashboard.Windows.Select(window => (window.WithinDays, window.Units, window.ValueCents)));

        var first = dashboard.Batches[0];
        Assert.Equal("Energético 473ml", first.ProductName);
        Assert.Equal(2, first.DaysLeft);
        Assert.Equal(3, dashboard.Batches.Count);
    }

    [Fact]
    public async Task The_loss_report_adds_expired_and_damaged_goods_at_cost()
    {
        var drinks = _stock.Seed(_drink.Id, 10, 500, Today.AddDays(-1), Now);
        var soaps = _stock.Seed(_soap.Id, 10, 300, null, Now);
        RecordLoss(StockMovement.ExpiryLoss(drinks, 4, Now));
        RecordLoss(StockMovement.Outflow(MovementType.DamageLoss, soaps, 2, Now, null));
        RecordLoss(StockMovement.Outflow(MovementType.DamageLoss, drinks, 1, Now, null));

        var report = await _reports.LossesAsync(Today, Today, Token);

        Assert.Equal(7, report.Units);
        Assert.Equal(4 * 500 + 2 * 300 + 500, report.ValueCents);

        Assert.Equal(2000, report.ByType.Single(loss => loss.Type == MovementType.ExpiryLoss).ValueCents);
        Assert.Equal(1100, report.ByType.Single(loss => loss.Type == MovementType.DamageLoss).ValueCents);

        // Grouped by the top of each branch: an energy drink counts under Beverages.
        Assert.Equal(["Bebidas", "Limpeza"], report.ByCategory.Select(loss => loss.CategoryName));
        Assert.Equal(2500, report.ByCategory[0].ValueCents);

        Assert.Equal("Energético 473ml", report.TopProducts[0].ProductName);
    }

    [Fact]
    public async Task Returns_to_the_supplier_are_not_losses()
    {
        var soaps = _stock.Seed(_soap.Id, 10, 300, null, Now);
        RecordLoss(StockMovement.Outflow(MovementType.ReturnToSupplier, soaps, 5, Now, null));

        var report = await _reports.LossesAsync(Today, Today, Token);

        Assert.Equal(0, report.ValueCents);
        Assert.Empty(report.ByType);
    }

    [Fact]
    public async Task Losses_outside_the_period_are_left_out()
    {
        var drinks = _stock.Seed(_drink.Id, 10, 500, null, Now);
        RecordLoss(StockMovement.Outflow(MovementType.DamageLoss, drinks, 1, Now.AddDays(-40), null));

        var report = await _reports.LossesAsync(Today.AddDays(-7), Today, Token);

        Assert.Equal(0, report.Units);
    }

    [Theory]
    [InlineData(1, 0)]     // ends before it starts
    [InlineData(0, 400)]   // more than a year
    public async Task A_report_period_has_to_make_sense(int startOffset, int endOffset)
    {
        await Refused.WithAsync(
            ErrorKind.Invalid,
            ErrorCodes.ReportPeriodInvalid,
            () => _reports.LossesAsync(Today.AddDays(startOffset), Today.AddDays(endOffset), Token));
    }

    private void RecordLoss(StockMovement movement) => _stock.Movements.Add(movement);

    private Product Add(string name, string barcode, Category category)
    {
        var product = Product.Create(Shop, name, category.Id, UnitOfMeasure.Unit, Money.FromCents(1000), Gtin.Parse(barcode));
        _products.AddAsync(product).GetAwaiter().GetResult();
        return product;
    }
}
