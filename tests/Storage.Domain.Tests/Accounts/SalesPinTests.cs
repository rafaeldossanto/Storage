using Storage.Domain.Accounts;

namespace Storage.Domain.Tests.Accounts;

public class SalesPinTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static Tenant ShopWithPin()
    {
        var shop = Tenant.Create("Mercadinho");
        shop.SetSalesPin("hash");
        return shop;
    }

    [Fact]
    public void Five_wrong_pins_in_a_row_lock_the_sales_area_for_fifteen_minutes()
    {
        var shop = ShopWithPin();

        for (var attempt = 1; attempt < Tenant.SalesPinMaxFailures; attempt++)
        {
            shop.RecordSalesPinFailure(Now);
            Assert.False(shop.IsSalesPinLocked(Now));
        }

        shop.RecordSalesPinFailure(Now);

        Assert.True(shop.IsSalesPinLocked(Now));
        Assert.True(shop.IsSalesPinLocked(Now.AddMinutes(14)));
        Assert.False(shop.IsSalesPinLocked(Now.AddMinutes(16)));
    }

    [Fact]
    public void A_right_pin_forgets_the_wrong_ones_before_it()
    {
        var shop = ShopWithPin();
        shop.RecordSalesPinFailure(Now);
        shop.RecordSalesPinFailure(Now);

        Assert.True(shop.RecordSalesPinSuccess());
        Assert.Equal(0, shop.SalesPinFailures);

        // Nothing left to forget: nothing to save either.
        Assert.False(shop.RecordSalesPinSuccess());
    }

    [Fact]
    public void Every_change_to_the_pin_state_moves_its_version()
    {
        var shop = Tenant.Create("Mercadinho");
        Assert.False(shop.HasSalesPin);

        shop.SetSalesPin("hash");
        shop.RecordSalesPinFailure(Now);
        shop.RecordSalesPinSuccess();

        Assert.True(shop.HasSalesPin);
        Assert.Equal(3, shop.SalesPinVersion);
    }
}
