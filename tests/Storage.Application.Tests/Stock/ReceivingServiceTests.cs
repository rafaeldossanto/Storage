using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Application.Stock;
using Storage.Application.Tests.Fakes;
using Storage.Domain.Catalog;
using Storage.Domain.Common;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Tests.Stock;

public sealed class ReceivingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 26);
    private static readonly DateOnly December = new(2026, 12, 1);
    private static readonly Guid Shop = Guid.CreateVersion7();
    private static readonly Guid Operator = Guid.CreateVersion7();

    private readonly ManualClock _clock = new(Now);
    private readonly InMemoryStockStore _stock = new(Shop);
    private readonly InMemorySupplierRepository _suppliers;
    private readonly ReceivingService _service;
    private readonly Product _drink;
    private readonly Product _soap;

    public ReceivingServiceTests()
    {
        var tenant = new FixedTenant(Shop);
        var products = new InMemoryProductRepository(tenant);
        _suppliers = new InMemorySupplierRepository(tenant);
        _service = new ReceivingService(
            _stock, products, _suppliers, new FixedCalendar(Today), tenant, new FixedUser(Operator), _clock);

        _drink = Product.Create(Shop, "Energético 473ml", Guid.CreateVersion7(), UnitOfMeasure.Unit,
            Money.FromCents(899), Gtin.Parse("7891000000014"));
        _drink.AddPackaging(Gtin.Parse("17891000000011"), "Fardo 12", 12);

        _soap = Product.Create(Shop, "Sabão em barra", Guid.CreateVersion7(), UnitOfMeasure.Unit,
            Money.FromCents(450), Gtin.Parse("7891000000021"));
        _soap.SetExpiryTracking(false);

        products.AddAsync(_drink).GetAwaiter().GetResult();
        products.AddAsync(_soap).GetAwaiter().GetResult();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_delivery_becomes_one_batch_and_one_movement_per_line_in_a_single_commit()
    {
        var receipt = await _service.ReceiveAsync(
            new ReceiveGoodsRequest([
                new("17891000000011", 2, 6000, December),
                new("7891000000021", 10, 450),
            ]),
            Token);

        Assert.Equal(1, _stock.Commits);
        Assert.Equal(2, _stock.Batches.Count);
        Assert.Equal(2, _stock.Movements.Count);
        Assert.All(_stock.Movements, movement =>
        {
            Assert.Equal(MovementType.Receipt, movement.Type);
            Assert.Equal(Operator, movement.UserId);
            Assert.Equal(receipt.Id, movement.DocumentId);
        });

        Assert.IsType<GoodsReceipt>(Assert.Single(_stock.Documents));
        Assert.Equal(16500, receipt.TotalCostCents);
    }

    [Fact]
    public async Task A_scanned_pack_goes_into_stock_as_cans()
    {
        var receipt = await _service.ReceiveAsync(
            new ReceiveGoodsRequest([new("17891000000011", 2, 6000, December)]), Token);

        var line = Assert.Single(receipt.Lines);
        Assert.Equal("Energético 473ml", line.ProductName);
        Assert.Equal(24, line.BaseUnits);
        Assert.Equal(500, line.UnitCostCents);
        Assert.Equal(24, Assert.Single(_stock.Batches).RemainingQuantity);
    }

    [Fact]
    public async Task A_refused_line_says_which_line_it_was_and_nothing_is_committed()
    {
        var refusal = await Assert.ThrowsAsync<DomainException>(() => _service.ReceiveAsync(
            new ReceiveGoodsRequest([
                new("7891000000021", 10, 450),
                new("7891000000014", 6, 500, Today.AddDays(-3)),
            ]),
            Token));

        Assert.Equal(DomainErrors.ReceiptAlreadyExpired, refusal.Code);
        Assert.Equal(2, refusal.Data[ReceivingService.LineKey]);
        Assert.Equal(0, _stock.Commits);
    }

    [Fact]
    public async Task An_unknown_barcode_is_not_created_on_the_fly()
    {
        var refusal = await Assert.ThrowsAsync<UseCaseException>(() => _service.ReceiveAsync(
            new ReceiveGoodsRequest([new("7891000000038", 1, 100)]), Token));

        Assert.Equal(ErrorCodes.ProductNotFound, refusal.Code);
        Assert.Equal(1, refusal.Data[ReceivingService.LineKey]);
    }

    [Fact]
    public async Task A_barcode_with_a_wrong_check_digit_names_its_line()
    {
        var refusal = await Assert.ThrowsAsync<UseCaseException>(() => _service.ReceiveAsync(
            new ReceiveGoodsRequest([new("7891000000021", 1, 450), new("7891000000015", 1, 100)]), Token));

        Assert.Equal(ErrorCodes.BarcodeInvalid, refusal.Code);
        Assert.Equal(2, refusal.Data[ReceivingService.LineKey]);
    }

    [Fact]
    public async Task An_empty_delivery_is_refused()
    {
        await Refused.WithAsync(
            ErrorKind.Invalid,
            ErrorCodes.ReceiptEmpty,
            () => _service.ReceiveAsync(new ReceiveGoodsRequest([]), Token));
    }

    [Fact]
    public async Task A_supplier_that_does_not_exist_is_not_found()
    {
        await Refused.WithAsync(
            ErrorKind.NotFound,
            ErrorCodes.SupplierNotFound,
            () => _service.ReceiveAsync(
                new ReceiveGoodsRequest([new("7891000000021", 1, 450)], SupplierId: Guid.CreateVersion7()), Token));
    }

    [Fact]
    public async Task Recent_deliveries_come_back_with_their_supplier_newest_first()
    {
        var supplier = Supplier.Create(Shop, "Distribuidora Boa Vista");
        await _suppliers.AddAsync(supplier, Token);

        var received = await _service.ReceiveAsync(
            new ReceiveGoodsRequest([new("7891000000021", 10, 450)], supplier.Id, "NF 998"), Token);

        var recent = (await _service.ListAsync(PageRequest.First(), Token)).Items;

        var summary = Assert.Single(recent);
        Assert.Equal(received.Id, summary.Id);
        Assert.Equal("Distribuidora Boa Vista", summary.SupplierName);
        Assert.Equal("NF 998", summary.InvoiceNumber);
        Assert.Equal(4500, summary.TotalCostCents);
    }

    [Fact]
    public async Task A_delivery_can_be_read_back_line_by_line()
    {
        var received = await _service.ReceiveAsync(
            new ReceiveGoodsRequest([new("17891000000011", 1, 6000, December)]), Token);

        var reread = await _service.GetAsync(received.Id, Token);

        Assert.Equal("Energético 473ml", Assert.Single(reread.Lines).ProductName);
        Assert.Equal("17891000000011", reread.Lines[0].Barcode);
    }

    [Fact]
    public async Task A_receipt_typed_wrong_is_taken_back_and_its_goods_leave_the_shelf()
    {
        var receipt = await _service.ReceiveAsync(new ReceiveGoodsRequest([new("17891000000011", 2, 6000, December)]), Token);
        _clock.Advance(TimeSpan.FromMinutes(3));

        var cancelled = await _service.CancelAsync(receipt.Id, Token);

        Assert.Equal(GoodsReceiptStatus.Cancelled, cancelled.Status);
        var batch = Assert.Single(_stock.Batches);
        Assert.Equal(0, batch.RemainingQuantity);
        var back = Assert.Single(_stock.Movements, movement => movement.Type == MovementType.ReceiptCancellation);
        Assert.Equal(-24, back.Quantity);
        Assert.Equal(receipt.Id, back.DocumentId);
    }

    [Fact]
    public async Task A_receipt_whose_goods_started_to_sell_stays()
    {
        var receipt = await _service.ReceiveAsync(new ReceiveGoodsRequest([new("7891000000021", 10, 450)]), Token);
        _stock.Batches[0].Take(1);

        await Refused.WithAsync(ErrorKind.Conflict, ErrorCodes.ReceiptStockMoved, () => _service.CancelAsync(receipt.Id, Token));
    }

    [Fact]
    public async Task After_ten_minutes_a_receipt_stays()
    {
        var receipt = await _service.ReceiveAsync(new ReceiveGoodsRequest([new("7891000000021", 10, 450)]), Token);
        _clock.Advance(GoodsReceipt.CancellationWindow + TimeSpan.FromSeconds(1));

        var refusal = await Assert.ThrowsAsync<DomainException>(() => _service.CancelAsync(receipt.Id, Token));

        Assert.Equal(DomainErrors.ReceiptCancelWindowClosed, refusal.Code);
    }
}
