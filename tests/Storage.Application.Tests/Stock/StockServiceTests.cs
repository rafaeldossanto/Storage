using Storage.Application.Errors;
using Storage.Application.Stock;
using Storage.Application.Tests.Fakes;
using Storage.Domain.Catalog;
using Storage.Domain.Common;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Tests.Stock;

public sealed class StockServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Shop = Guid.CreateVersion7();
    private static readonly Guid Operator = Guid.CreateVersion7();

    private readonly InMemoryStockStore _stock = new(Shop);
    private readonly InMemoryProductRepository _products;
    private readonly StockService _service;
    private readonly Product _drink;

    public StockServiceTests()
    {
        var tenant = new FixedTenant(Shop);
        _products = new InMemoryProductRepository(tenant);
        _service = new StockService(_stock, _products, new FixedUser(Operator), new ManualClock(Now));

        _drink = Product.Create(Shop, "Energético 473ml", Guid.CreateVersion7(), UnitOfMeasure.Unit,
            Money.FromCents(899), Gtin.Parse("7891000000014"));
        _products.AddAsync(_drink).GetAwaiter().GetResult();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Damage_comes_off_the_batch_that_expires_first()
    {
        var late = _stock.Seed(_drink.Id, 10, 500, new DateOnly(2026, 12, 1), Now);
        var soon = _stock.Seed(_drink.Id, 10, 450, new DateOnly(2026, 10, 1), Now);

        await _service.RemoveAsync(_drink.Id, new RemoveStockRequest(3, StockRemovalReason.Damage, "Lata amassada"), Token);

        Assert.Equal(7, soon.RemainingQuantity);
        Assert.Equal(10, late.RemainingQuantity);

        var movement = Assert.Single(_stock.Movements);
        Assert.Equal(MovementType.DamageLoss, movement.Type);
        Assert.Equal(-3, movement.Quantity);
        Assert.Equal(-1350, movement.Value.Cents);
        Assert.Equal(Operator, movement.UserId);
        Assert.Equal("Lata amassada", movement.Note);
    }

    [Fact]
    public async Task A_removal_bigger_than_one_batch_writes_one_movement_per_batch_in_a_single_commit()
    {
        _stock.Seed(_drink.Id, 2, 450, new DateOnly(2026, 10, 1), Now);
        _stock.Seed(_drink.Id, 10, 500, new DateOnly(2026, 12, 1), Now);

        var level = await _service.RemoveAsync(_drink.Id, new RemoveStockRequest(5, StockRemovalReason.ReturnToSupplier), Token);

        Assert.Equal(2, _stock.Movements.Count);
        Assert.All(_stock.Movements, movement => Assert.Equal(MovementType.ReturnToSupplier, movement.Type));
        Assert.Equal(1, _stock.Commits);
        Assert.Equal(7, level.Quantity);
    }

    [Fact]
    public async Task The_level_that_comes_back_reflects_the_removal()
    {
        _stock.Seed(_drink.Id, 10, 500, null, Now);

        var level = await _service.RemoveAsync(_drink.Id, new RemoveStockRequest(4, StockRemovalReason.Damage), Token);

        Assert.Equal(6, level.Quantity);
        Assert.Equal(3000, level.StockValueCents);
        Assert.Equal(500, level.AverageCostCents);
    }

    [Fact]
    public async Task Removing_more_than_the_shelf_holds_is_refused_and_nothing_is_written()
    {
        _stock.Seed(_drink.Id, 2, 500, null, Now);

        var refusal = await Assert.ThrowsAsync<DomainException>(
            () => _service.RemoveAsync(_drink.Id, new RemoveStockRequest(3, StockRemovalReason.Damage), Token));

        Assert.Equal(DomainErrors.StockInsufficient, refusal.Code);
        Assert.Equal(0, _stock.Commits);
    }

    [Fact]
    public async Task At_least_one_unit_must_be_removed()
    {
        await Refused.WithAsync(
            ErrorKind.Invalid,
            ErrorCodes.StockQuantityInvalid,
            () => _service.RemoveAsync(_drink.Id, new RemoveStockRequest(0, StockRemovalReason.Damage), Token));
    }

    [Fact]
    public async Task A_product_of_another_shop_is_not_found()
    {
        await Refused.WithAsync(
            ErrorKind.NotFound,
            ErrorCodes.ProductNotFound,
            () => _service.RemoveAsync(Guid.CreateVersion7(), new RemoveStockRequest(1, StockRemovalReason.Damage), Token));
    }

    [Fact]
    public async Task A_note_is_limited_in_length()
    {
        _stock.Seed(_drink.Id, 10, 500, null, Now);

        await Refused.WithAsync(
            ErrorKind.Invalid,
            ErrorCodes.StockNoteTooLong,
            () => _service.RemoveAsync(
                _drink.Id,
                new RemoveStockRequest(1, StockRemovalReason.Damage, new string('x', StockService.NoteMaxLength + 1)),
                Token));
    }
}
