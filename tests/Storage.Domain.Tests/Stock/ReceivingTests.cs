using Storage.Domain.Catalog;
using Storage.Domain.Common;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Tests.Stock;

public class GoodsReceiptTests
{
    private static readonly Guid Shop = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 26);
    private static readonly Gtin Can = Gtin.Parse("7891000000014");
    private static readonly Gtin Pack = Gtin.Parse("17891000000011");

    private static Product EnergyDrink(bool tracksExpiry = true)
    {
        var product = Product.Create(Shop, "Energético 473ml", Guid.CreateVersion7(), UnitOfMeasure.Unit, Money.FromCents(899), Can);
        product.AddPackaging(Pack, "Fardo 12", 12);
        product.SetExpiryTracking(tracksExpiry);
        return product;
    }

    private static GoodsReceipt Open() => GoodsReceipt.Open(Shop, null, "NF 1234", null, Guid.CreateVersion7(), Now);

    [Fact]
    public void Scanning_a_pack_shelves_its_units_at_the_cost_of_one_unit()
    {
        var receipt = Open();

        // Two twelve-packs at R$ 60,00 each: 24 cans at R$ 5,00.
        var batch = receipt.Receive(EnergyDrink(), Pack, 2, Money.FromCents(6000), new DateOnly(2026, 12, 1), Today);

        Assert.Equal(24, batch.InitialQuantity);
        Assert.Equal(500, batch.UnitCost.Cents);
        Assert.Equal(receipt.Id, batch.ReceiptId);

        var line = Assert.Single(receipt.Lines);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(24, line.BaseUnits);
        Assert.Equal(batch.Id, line.BatchId);
    }

    [Fact]
    public void The_receipt_total_is_the_invoice_not_the_rounded_unit_costs()
    {
        var receipt = Open();

        // R$ 10,00 for a pack of 12 is 83.33 cents a can; the shelf rounds, the invoice does not.
        var batch = receipt.Receive(EnergyDrink(), Pack, 3, Money.FromCents(1000), new DateOnly(2026, 12, 1), Today);

        Assert.Equal(83, batch.UnitCost.Cents);
        Assert.Equal(3000, receipt.TotalCost.Cents);
    }

    [Fact]
    public void A_product_that_tracks_expiry_needs_a_date()
    {
        DomainAssert.Breaks(
            DomainErrors.ReceiptExpiryRequired,
            () => Open().Receive(EnergyDrink(), Can, 10, Money.FromCents(500), expiryDate: null, Today));
    }

    [Fact]
    public void Goods_that_do_not_spoil_come_in_without_a_date()
    {
        var batch = Open().Receive(EnergyDrink(tracksExpiry: false), Can, 10, Money.FromCents(500), null, Today);

        Assert.Null(batch.ExpiryDate);
    }

    [Fact]
    public void A_date_already_past_is_refused_but_today_is_fine()
    {
        DomainAssert.Breaks(
            DomainErrors.ReceiptAlreadyExpired,
            () => Open().Receive(EnergyDrink(), Can, 10, Money.FromCents(500), Today.AddDays(-1), Today));

        // Expiring today still means sellable all day today.
        Assert.NotNull(Open().Receive(EnergyDrink(), Can, 10, Money.FromCents(500), Today, Today));
    }

    [Fact]
    public void At_least_one_unit_and_a_cost_that_is_not_negative()
    {
        DomainAssert.Breaks(
            DomainErrors.ReceiptQuantityInvalid,
            () => Open().Receive(EnergyDrink(), Can, 0, Money.FromCents(500), Today, Today));

        DomainAssert.Breaks(
            DomainErrors.BatchCostNegative,
            () => Open().Receive(EnergyDrink(), Can, 1, Money.FromCents(-1), Today, Today));
    }

    [Fact]
    public void A_barcode_that_is_not_the_products_is_refused()
    {
        DomainAssert.Breaks(
            DomainErrors.ProductBarcodeNotOnProduct,
            () => Open().Receive(EnergyDrink(), Gtin.Parse("7891000000021"), 1, Money.FromCents(500), Today, Today));
    }

    [Fact]
    public void A_receipt_cannot_take_in_another_shops_product()
    {
        var foreign = Product.Create(Guid.CreateVersion7(), "Outro", Guid.CreateVersion7(), UnitOfMeasure.Unit, Money.FromCents(100), Can);
        foreign.SetExpiryTracking(false);

        Assert.Throws<InvalidOperationException>(() => Open().Receive(foreign, Can, 1, Money.FromCents(100), null, Today));
    }
}

public class SupplierTests
{
    [Fact]
    public void A_supplier_needs_a_name()
    {
        DomainAssert.Breaks(DomainErrors.SupplierNameInvalid, () => Supplier.Create(Guid.CreateVersion7(), " "));
    }

    [Fact]
    public void The_new_alphanumeric_CNPJ_is_accepted_as_typed()
    {
        // Since July 2026 a CNPJ may contain letters; a digits-only check would refuse it.
        var supplier = Supplier.Create(Guid.CreateVersion7(), "Distribuidora Boa Vista", "12.ABC.345/01DE-35");

        Assert.Equal("12.ABC.345/01DE-35", supplier.TaxId);
    }

    [Fact]
    public void Blank_optional_fields_are_stored_as_nothing()
    {
        var supplier = Supplier.Create(Guid.CreateVersion7(), "Distribuidora", taxId: "  ", contact: "");

        Assert.Null(supplier.TaxId);
        Assert.Null(supplier.Contact);
    }
}

public class MoneyDivisionTests
{
    [Theory]
    [InlineData(6000, 12, 500)]
    [InlineData(1000, 12, 83)]  // 83.33
    [InlineData(1000, 16, 63)]  // 62.5 rounds up, like a price tag
    [InlineData(899, 1, 899)]
    public void Dividing_rounds_to_the_nearest_cent(long cents, int units, long expected)
    {
        Assert.Equal(expected, Money.FromCents(cents).DividedBy(units).Cents);
    }
}
