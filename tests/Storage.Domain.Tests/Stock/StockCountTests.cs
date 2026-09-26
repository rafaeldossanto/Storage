using Storage.Domain.Common;
using Storage.Domain.Stock;

namespace Storage.Domain.Tests.Stock;

public class StockCountTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Drink = Guid.CreateVersion7();
    private static readonly Guid Soap = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();

    private static StockCount Open() => StockCount.Start(Guid.CreateVersion7(), null, Owner, Now);

    [Fact]
    public void Scans_add_up_and_a_typed_quantity_replaces_them()
    {
        var count = Open();

        count.AddCounted(Drink, 12);
        count.AddCounted(Drink, 3);
        count.SetCounted(Soap, 7);
        count.SetCounted(Soap, 5);

        Assert.Equal(15, count.Items.Single(item => item.ProductId == Drink).CountedQuantity);
        Assert.Equal(5, count.Items.Single(item => item.ProductId == Soap).CountedQuantity);
        Assert.Equal(4, count.Version);
    }

    [Fact]
    public void Closing_compares_the_shelf_with_the_ledger()
    {
        var count = Open();
        count.SetCounted(Drink, 8);
        count.SetCounted(Soap, 5);

        var differences = count.Close(new Dictionary<Guid, int> { [Drink] = 10, [Soap] = 5 }, "Duas latas furtadas", Owner, Now);

        var missing = Assert.Single(differences);
        Assert.Equal(-2, missing.Difference);
        Assert.Equal(StockCountStatus.Closed, count.Status);
        Assert.Equal(-2, count.Items.Single(item => item.ProductId == Drink).Difference);
        Assert.Equal(0, count.Items.Single(item => item.ProductId == Soap).Difference);
    }

    [Fact]
    public void A_count_that_differs_needs_a_reason()
    {
        var count = Open();
        count.SetCounted(Drink, 8);

        DomainAssert.Breaks(
            DomainErrors.CountJustificationRequired,
            () => count.Close(new Dictionary<Guid, int> { [Drink] = 10 }, "   ", Owner, Now));

        Assert.Equal(StockCountStatus.Open, count.Status);
    }

    [Fact]
    public void A_count_that_matches_closes_without_one()
    {
        var count = Open();
        count.SetCounted(Drink, 10);

        Assert.Empty(count.Close(new Dictionary<Guid, int> { [Drink] = 10 }, null, Owner, Now));
        Assert.Null(count.Justification);
    }

    [Fact]
    public void A_product_the_ledger_never_heard_of_counts_as_found()
    {
        var count = Open();
        count.SetCounted(Drink, 4);

        var found = Assert.Single(count.Close(new Dictionary<Guid, int>(), "Caixa esquecida no depósito", Owner, Now));

        Assert.Equal(4, found.Difference);
    }

    [Fact]
    public void Nothing_counted_cannot_be_closed()
    {
        DomainAssert.Breaks(DomainErrors.CountEmpty, () => Open().Close(new Dictionary<Guid, int>(), null, Owner, Now));
    }

    [Fact]
    public void A_closed_count_takes_no_more_scans_and_cannot_close_again()
    {
        var count = Open();
        count.SetCounted(Drink, 10);
        count.Close(new Dictionary<Guid, int> { [Drink] = 10 }, null, Owner, Now);

        DomainAssert.Breaks(DomainErrors.CountNotOpen, () => count.AddCounted(Drink, 1));
        DomainAssert.Breaks(DomainErrors.CountNotOpen, () => count.Close(new Dictionary<Guid, int>(), null, Owner, Now));
    }

    [Fact]
    public void A_negative_count_is_refused()
    {
        DomainAssert.Breaks(DomainErrors.CountQuantityInvalid, () => Open().SetCounted(Drink, -1));
    }
}
