using Storage.Application.Errors;
using Storage.Application.Stock;
using Storage.Application.Tests.Fakes;
using Storage.Domain.Catalog;
using Storage.Domain.Common;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Tests.Stock;

public sealed class CountServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Shop = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();

    private readonly InMemoryStockStore _stock = new(Shop);
    private readonly InMemoryCountStore _counts;
    private readonly CountService _service;
    private readonly Category _beverages;
    private readonly Product _drink;
    private readonly Product _soap;

    public CountServiceTests()
    {
        var tenant = new FixedTenant(Shop);
        var categories = new InMemoryCategoryRepository(tenant);
        var products = new InMemoryProductRepository(tenant, categories);
        _counts = new InMemoryCountStore(tenant);
        _service = new CountService(_counts, _stock, products, categories, tenant, new FixedUser(Owner), new ManualClock(Now));

        _beverages = Category.CreateRoot(Shop, "Bebidas");
        var cleaning = Category.CreateRoot(Shop, "Limpeza");
        categories.Seed(_beverages);
        categories.Seed(cleaning);

        _drink = Product.Create(Shop, "Energético 473ml", _beverages.Id, UnitOfMeasure.Unit, Money.FromCents(899), Gtin.Parse("7891000000014"));
        _drink.AddPackaging(Gtin.Parse("17891000000011"), "Fardo 12", 12);
        _soap = Product.Create(Shop, "Sabão em barra", cleaning.Id, UnitOfMeasure.Unit, Money.FromCents(450), Gtin.Parse("7891000000021"));
        products.AddAsync(_drink).GetAwaiter().GetResult();
        products.AddAsync(_soap).GetAwaiter().GetResult();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_one_count_is_open_at_a_time()
    {
        await _service.StartAsync(new StartCountRequest(), Token);

        await Refused.WithAsync(
            ErrorKind.Conflict, ErrorCodes.CountAlreadyOpen, () => _service.StartAsync(new StartCountRequest(), Token));
    }

    [Fact]
    public async Task Scanning_a_pack_counts_its_units_and_shows_the_live_difference()
    {
        _stock.Seed(_drink.Id, 30, 500, null, Now);
        var count = await _service.StartAsync(new StartCountRequest(), Token);

        await _service.ScanAsync(count.Id, new ScanRequest("17891000000011", 2), Token);
        var after = await _service.ScanAsync(count.Id, new ScanRequest("7891000000014"), Token);

        var item = Assert.Single(after.Items);
        Assert.Equal(25, item.Counted);
        Assert.Equal(30, item.System);
        Assert.Equal(-5, item.Difference);
    }

    [Fact]
    public async Task A_count_of_one_branch_refuses_what_belongs_elsewhere()
    {
        var count = await _service.StartAsync(new StartCountRequest(_beverages.Id), Token);

        await Refused.WithAsync(
            ErrorKind.Invalid,
            ErrorCodes.CountProductOutOfScope,
            () => _service.ScanAsync(count.Id, new ScanRequest("7891000000021"), Token));
    }

    [Fact]
    public async Task Missing_units_come_out_of_the_batches_that_expire_first()
    {
        var soon = _stock.Seed(_drink.Id, 4, 450, new DateOnly(2026, 10, 1), Now);
        var late = _stock.Seed(_drink.Id, 10, 500, new DateOnly(2026, 12, 1), Now);
        var count = await _service.StartAsync(new StartCountRequest(), Token);
        await _service.SetCountedAsync(count.Id, _drink.Id, new SetCountedRequest(9), Token);

        var closed = await _service.CloseAsync(count.Id, new CloseCountRequest("Furto na gôndola"), Token);

        Assert.Equal(StockCountStatus.Closed, closed.Status);
        Assert.Equal(0, soon.RemainingQuantity);
        Assert.Equal(9, late.RemainingQuantity);

        Assert.All(_stock.Movements, movement =>
        {
            Assert.Equal(MovementType.CountAdjustment, movement.Type);
            Assert.Equal(count.Id, movement.DocumentId);
            Assert.Equal("Furto na gôndola", movement.Note);
            Assert.Equal(Owner, movement.UserId);
        });
        Assert.Equal(-5, _stock.Movements.Sum(movement => movement.Quantity));
    }

    [Fact]
    public async Task Found_units_become_a_batch_at_the_current_average_cost()
    {
        _stock.Seed(_drink.Id, 10, 500, null, Now);
        _stock.Seed(_drink.Id, 10, 600, null, Now);
        var count = await _service.StartAsync(new StartCountRequest(), Token);
        await _service.SetCountedAsync(count.Id, _drink.Id, new SetCountedRequest(23), Token);

        await _service.CloseAsync(count.Id, new CloseCountRequest("Fardo achado no depósito"), Token);

        var found = Assert.Single(_stock.Batches, batch => batch.InitialQuantity == 3);
        Assert.Equal(550, found.UnitCost.Cents);
        Assert.Null(found.ExpiryDate);
        Assert.Equal(3, Assert.Single(_stock.Movements).Quantity);
    }

    [Fact]
    public async Task Closing_goes_out_in_the_same_commit_as_the_adjustments()
    {
        _stock.Seed(_drink.Id, 10, 500, null, Now);
        var count = await _service.StartAsync(new StartCountRequest(), Token);
        await _service.SetCountedAsync(count.Id, _drink.Id, new SetCountedRequest(8), Token);

        await _service.CloseAsync(count.Id, new CloseCountRequest("Avaria não registrada"), Token);

        Assert.Equal(1, _stock.Commits);
    }

    [Fact]
    public async Task A_difference_without_a_reason_leaves_the_stock_untouched()
    {
        var batch = _stock.Seed(_drink.Id, 10, 500, null, Now);
        var count = await _service.StartAsync(new StartCountRequest(), Token);
        await _service.SetCountedAsync(count.Id, _drink.Id, new SetCountedRequest(8), Token);

        var refusal = await Assert.ThrowsAsync<DomainException>(
            () => _service.CloseAsync(count.Id, new CloseCountRequest(null), Token));

        Assert.Equal(DomainErrors.CountJustificationRequired, refusal.Code);
        Assert.Equal(10, batch.RemainingQuantity);
        Assert.Equal(0, _stock.Commits);
    }

    [Fact]
    public async Task A_cancelled_count_takes_no_more_scans()
    {
        var count = await _service.StartAsync(new StartCountRequest(), Token);
        await _service.CancelAsync(count.Id, Token);

        var refusal = await Assert.ThrowsAsync<DomainException>(
            () => _service.ScanAsync(count.Id, new ScanRequest("7891000000014"), Token));

        Assert.Equal(DomainErrors.CountNotOpen, refusal.Code);
    }
}
