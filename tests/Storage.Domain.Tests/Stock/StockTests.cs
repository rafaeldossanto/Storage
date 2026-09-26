using Storage.Domain.Common;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Tests.Stock;

public class BatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    internal static Batch Receive(int quantity = 10, long unitCostCents = 500, DateOnly? expiry = null, DateTimeOffset? at = null) =>
        Batch.Receive(Guid.Empty, Guid.Empty, quantity, Money.FromCents(unitCostCents), expiry, at ?? Now);

    [Fact]
    public void A_new_batch_holds_everything_it_received()
    {
        var batch = Receive(12);

        Assert.Equal(12, batch.RemainingQuantity);
        Assert.True(batch.IsAvailable);
        Assert.Equal(6000, batch.Value.Cents);
    }

    [Fact]
    public void A_batch_needs_at_least_one_unit_and_a_cost_that_is_not_negative()
    {
        DomainAssert.Breaks(DomainErrors.BatchQuantityInvalid, () => Receive(0));
        DomainAssert.Breaks(DomainErrors.BatchCostNegative, () => Receive(unitCostCents: -1));
    }

    [Fact]
    public void Taking_every_unit_depletes_the_batch()
    {
        var batch = Receive(3);

        batch.Take(3);

        Assert.Equal(BatchStatus.Depleted, batch.Status);
        Assert.False(batch.IsAvailable);
    }

    [Fact]
    public void Taking_more_than_is_there_is_a_bug_in_the_caller()
    {
        Assert.Throws<InvalidOperationException>(() => Receive(3).Take(4));
    }

    [Fact]
    public void A_batch_is_still_sellable_all_through_its_expiry_date()
    {
        var batch = Receive(expiry: new DateOnly(2026, 9, 18));

        Assert.False(batch.HasExpiredOn(new DateOnly(2026, 9, 18)));
        Assert.True(batch.HasExpiredOn(new DateOnly(2026, 9, 19)));
    }

    [Fact]
    public void Goods_without_a_date_never_expire()
    {
        Assert.False(Receive(expiry: null).HasExpiredOn(new DateOnly(2099, 1, 1)));
    }

    [Fact]
    public void Expiring_reports_what_was_lost_and_takes_the_batch_off_sale()
    {
        var batch = Receive(15, expiry: new DateOnly(2026, 9, 17));
        batch.Take(5);

        var lost = batch.Expire();

        Assert.Equal(10, lost);
        Assert.Equal(0, batch.RemainingQuantity);
        Assert.Equal(BatchStatus.Expired, batch.Status);
        Assert.Equal(15, batch.InitialQuantity);
    }
}

public class StockMovementTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_receipt_adds_what_the_batch_received()
    {
        var batch = BatchTests.Receive(12, unitCostCents: 500);

        var movement = StockMovement.Receipt(batch, Now, userId: Guid.CreateVersion7(), receiptId: null);

        Assert.Equal(12, movement.Quantity);
        Assert.Equal(6000, movement.Value.Cents);
    }

    [Fact]
    public void Losses_are_negative_and_valued_at_the_batch_cost()
    {
        var batch = BatchTests.Receive(12, unitCostCents: 500);

        var expired = StockMovement.ExpiryLoss(batch, 4, Now);
        var damaged = StockMovement.Outflow(MovementType.DamageLoss, batch, 2, Now, userId: null);

        Assert.Equal(-4, expired.Quantity);
        Assert.Equal(-2000, expired.Value.Cents);
        Assert.Null(expired.UserId);
        Assert.Equal(-2, damaged.Quantity);
    }

    [Fact]
    public void Receipts_and_expiry_losses_cannot_be_forged_as_generic_outflows()
    {
        var batch = BatchTests.Receive();

        Assert.Throws<ArgumentException>(() => StockMovement.Outflow(MovementType.Receipt, batch, 1, Now, null));
        Assert.Throws<ArgumentException>(() => StockMovement.Outflow(MovementType.ExpiryLoss, batch, 1, Now, null));
    }
}

public class FefoTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_batch_that_expires_first_goes_out_first()
    {
        var late = BatchTests.Receive(10, expiry: new DateOnly(2026, 12, 1));
        var soon = BatchTests.Receive(10, expiry: new DateOnly(2026, 10, 1));

        var plan = Fefo.Allocate([late, soon], 4);

        var (batch, quantity) = Assert.Single(plan);
        Assert.Same(soon, batch);
        Assert.Equal(4, quantity);
    }

    [Fact]
    public void Goods_that_never_expire_go_out_last()
    {
        var forever = BatchTests.Receive(10, expiry: null, at: Monday);
        var dated = BatchTests.Receive(10, expiry: new DateOnly(2027, 1, 1), at: Monday.AddDays(3));

        Assert.Same(dated, Fefo.Order([forever, dated])[0]);
    }

    [Fact]
    public void Among_equal_dates_the_oldest_delivery_goes_first()
    {
        var older = BatchTests.Receive(10, at: Monday);
        var newer = BatchTests.Receive(10, at: Monday.AddDays(2));

        Assert.Same(older, Fefo.Order([newer, older])[0]);
    }

    [Fact]
    public void A_quantity_bigger_than_one_batch_is_spread_across_several()
    {
        var soon = BatchTests.Receive(3, expiry: new DateOnly(2026, 10, 1));
        var late = BatchTests.Receive(10, expiry: new DateOnly(2026, 12, 1));

        var plan = Fefo.Allocate([late, soon], 5);

        Assert.Equal([(soon, 3), (late, 2)], plan);
    }

    [Fact]
    public void Batches_off_sale_are_ignored()
    {
        var expired = BatchTests.Receive(10, expiry: new DateOnly(2026, 9, 1));
        expired.Expire();
        var good = BatchTests.Receive(10, expiry: new DateOnly(2026, 12, 1));

        var (batch, _) = Assert.Single(Fefo.Allocate([expired, good], 2));
        Assert.Same(good, batch);
    }

    [Fact]
    public void Asking_for_more_than_the_stock_holds_is_refused()
    {
        DomainAssert.Breaks(DomainErrors.StockInsufficient, () => Fefo.Allocate([BatchTests.Receive(3)], 4));
    }
}

public class StockValuationTests
{
    [Fact]
    public void The_average_cost_is_weighted_by_quantity()
    {
        // 10 units at R$ 5,00 and 30 at R$ 6,00: R$ 230,00 for 40 units, R$ 5,75 each.
        var valuation = StockValuation.Of([
            BatchTests.Receive(10, unitCostCents: 500),
            BatchTests.Receive(30, unitCostCents: 600),
        ]);

        Assert.Equal(40, valuation.Quantity);
        Assert.Equal(23000, valuation.Value.Cents);
        Assert.Equal(575, valuation.AverageCost.Cents);
    }

    [Fact]
    public void The_average_rounds_half_a_cent_up()
    {
        // 3 units worth 1000 cents: 333.33 rounds to 333; 2 units worth 1001: 500.5 rounds to 501.
        Assert.Equal(333, new StockValuation(3, Money.FromCents(1000)).AverageCost.Cents);
        Assert.Equal(501, new StockValuation(2, Money.FromCents(1001)).AverageCost.Cents);
    }

    [Fact]
    public void An_empty_shelf_is_worth_nothing()
    {
        Assert.Equal(Money.Zero, StockValuation.Of([]).AverageCost);
    }
}
