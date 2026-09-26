using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Tests.Catalog;

public class ProductTests
{
    private const string CanBarcode = "7891000000014";
    private const string PackBarcode = "17891000000011";
    private const string OtherBarcode = "7891000000021";

    private static Product NewProduct(string barcode = CanBarcode) =>
        Product.Create(
            name: "Energético 473ml",
            categoryId: Guid.CreateVersion7(),
            baseUnit: UnitOfMeasure.Unit,
            salePrice: Money.FromDecimal(8.99m),
            gtin: Gtin.Parse(barcode));

    [Fact]
    public void A_new_product_answers_to_the_barcode_that_registered_it()
    {
        var product = NewProduct();

        var packaging = product.DefaultPackaging;

        Assert.Single(product.Packagings);
        Assert.True(packaging.IsDefault);
        Assert.Equal(1, packaging.ConversionFactor);
        Assert.Equal(Gtin.Parse(CanBarcode), packaging.Gtin);
    }

    [Fact]
    public void The_default_packaging_has_no_name_of_its_own()
    {
        // It is the product itself, so naming it here would put Portuguese in the domain.
        Assert.Null(NewProduct().DefaultPackaging.Name);
    }

    [Fact]
    public void Scanning_a_pack_moves_as_many_base_units_as_it_holds()
    {
        var product = NewProduct();
        product.AddPackaging(Gtin.Parse(PackBarcode), "Fardo 12", conversionFactor: 12);

        Assert.Equal(1, product.BaseUnitsFor(Gtin.Parse(CanBarcode)));
        Assert.Equal(12, product.BaseUnitsFor(Gtin.Parse(PackBarcode)));
    }

    [Fact]
    public void A_barcode_cannot_be_registered_twice_on_the_same_product()
    {
        var product = NewProduct();

        Assert.Throws<InvalidOperationException>(
            () => product.AddPackaging(Gtin.Parse(CanBarcode), "Fardo 12", 12));
    }

    [Fact]
    public void An_unknown_barcode_is_refused_rather_than_assumed_to_be_one_unit()
    {
        var product = NewProduct();

        Assert.Throws<InvalidOperationException>(
            () => product.BaseUnitsFor(Gtin.Parse(OtherBarcode)));
    }

    [Fact]
    public void A_packaging_needs_at_least_one_base_unit()
    {
        var product = NewProduct();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => product.AddPackaging(Gtin.Parse(PackBarcode), "Fardo", conversionFactor: 0));
    }

    [Fact]
    public void The_base_unit_packaging_cannot_be_removed()
    {
        var product = NewProduct();

        // Stock is counted in it: removing it would leave the balance without a unit.
        Assert.Throws<InvalidOperationException>(
            () => product.RemovePackaging(product.DefaultPackaging.Id));
    }

    [Fact]
    public void An_extra_packaging_can_be_removed()
    {
        var product = NewProduct();
        var pack = product.AddPackaging(Gtin.Parse(PackBarcode), "Fardo 12", 12);

        product.RemovePackaging(pack.Id);

        Assert.Single(product.Packagings);
        Assert.Null(product.FindPackaging(Gtin.Parse(PackBarcode)));
    }

    [Fact]
    public void A_price_is_kept_in_cents()
    {
        var product = NewProduct();

        product.ChangePrice(Money.FromDecimal(9.49m));

        Assert.Equal(949, product.SalePrice.Cents);
    }

    [Fact]
    public void A_negative_price_is_refused()
    {
        var product = NewProduct();

        Assert.Throws<ArgumentException>(() => product.ChangePrice(Money.FromDecimal(-1m)));
    }

    [Fact]
    public void A_negative_average_cost_is_refused()
    {
        var product = NewProduct();

        Assert.Throws<ArgumentException>(() => product.UpdateAverageCost(Money.FromCents(-1)));
    }

    [Fact]
    public void A_product_starts_tracking_expiry_and_active()
    {
        var product = NewProduct();

        Assert.True(product.TracksExpiry);
        Assert.True(product.Active);
        Assert.Equal(Money.Zero, product.AverageCost);
    }

    [Fact]
    public void Deactivating_keeps_the_product_for_past_sales()
    {
        var product = NewProduct();

        product.Deactivate();

        Assert.False(product.Active);
        Assert.Single(product.Packagings);
    }

    [Fact]
    public void An_internal_code_is_recognised_as_such()
    {
        var weighed = NewProduct("2001234005678");

        Assert.True(weighed.DefaultPackaging.IsInternalCode);
        Assert.False(NewProduct().DefaultPackaging.IsInternalCode);
    }
}
