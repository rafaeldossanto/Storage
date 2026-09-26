using Storage.Domain.Catalog;
using Storage.Domain.Common;
using Storage.Domain.Sales;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Tests.Sales;

public class SaleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Shop = Guid.CreateVersion7();

    private static readonly Product Drink = Product.Create(
        Shop, "Energético 473ml", Guid.CreateVersion7(), UnitOfMeasure.Unit, Money.FromCents(899), Gtin.Parse("7891000000014"));

    private static Batch BatchCosting(long cents, int quantity = 50) =>
        Batch.Receive(Shop, Drink.Id, quantity, Money.FromCents(cents), null, Now);

    [Fact]
    public void A_line_costs_what_its_very_batches_cost()
    {
        var sale = Sale.Start(Shop, Guid.CreateVersion7(), Now);

        // Three units from a batch bought at R$ 4,00 and two from one at R$ 5,00.
        sale.AddLine(Drink, Money.FromCents(899), [(BatchCosting(400), 3), (BatchCosting(500), 2)]);

        var line = Assert.Single(sale.Lines);
        Assert.Equal(5, line.Quantity);
        Assert.Equal(4495, line.Revenue.Cents);
        Assert.Equal(2200, line.Cost.Cents);
        Assert.Equal(2295, sale.Net.Cents);
    }

    [Fact]
    public void A_sale_keeps_the_name_the_product_had()
    {
        var sale = Sale.Start(Shop, Guid.CreateVersion7(), Now);
        sale.AddLine(Drink, Money.FromCents(899), [(BatchCosting(400), 1)]);

        Assert.Equal("Energético 473ml", sale.Lines[0].ProductName);
    }

    [Fact]
    public void An_empty_sale_is_refused()
    {
        DomainAssert.Breaks(DomainErrors.SaleEmpty, () => Sale.Start(Shop, Guid.CreateVersion7(), Now).RequireLines());
    }

    [Fact]
    public void A_sale_can_be_undone_for_ten_minutes_and_once()
    {
        var sale = Sale.Start(Shop, Guid.CreateVersion7(), Now);
        sale.AddLine(Drink, Money.FromCents(899), [(BatchCosting(400), 1)]);

        sale.Cancel(Guid.CreateVersion7(), Now.AddMinutes(9));

        Assert.Equal(SaleStatus.Cancelled, sale.Status);
        DomainAssert.Breaks(DomainErrors.SaleNotCompleted, () => sale.Cancel(Guid.CreateVersion7(), Now.AddMinutes(9)));
    }

    [Fact]
    public void After_ten_minutes_a_sale_stays_on_the_books()
    {
        var sale = Sale.Start(Shop, Guid.CreateVersion7(), Now);
        sale.AddLine(Drink, Money.FromCents(899), [(BatchCosting(400), 1)]);

        DomainAssert.Breaks(DomainErrors.SaleCancelWindowClosed, () => sale.Cancel(Guid.CreateVersion7(), Now.AddMinutes(11)));
        Assert.Equal(SaleStatus.Completed, sale.Status);
    }

    [Fact]
    public void Units_go_back_into_a_batch_that_was_emptied()
    {
        var batch = BatchCosting(400, quantity: 3);
        batch.Take(3);

        batch.Return(2);

        Assert.Equal(2, batch.RemainingQuantity);
        Assert.Equal(BatchStatus.Available, batch.Status);
    }

    [Fact]
    public void Units_do_not_go_back_into_an_expired_batch()
    {
        var batch = Batch.Receive(Shop, Drink.Id, 5, Money.FromCents(400), new DateOnly(2026, 9, 20), Now);
        batch.Take(2);
        batch.Expire();

        DomainAssert.Breaks(DomainErrors.BatchExpired, () => batch.Return(2));
    }
}
