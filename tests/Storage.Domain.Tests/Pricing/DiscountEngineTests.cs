using Storage.Domain.Catalog;
using Storage.Domain.Common;
using Storage.Domain.Pricing;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Tests.Pricing;

public class DiscountEngineTests
{
    private static readonly Guid Shop = Guid.CreateVersion7();

    // Saturday, 26 September 2026.
    private static readonly DateOnly Today = new(2026, 9, 26);

    private readonly Category _beverages = Category.CreateRoot(Shop, "Bebidas");
    private readonly Category _energy;
    private readonly Category _sodas;
    private readonly Product _energyDrink;
    private readonly Product _soda;

    public DiscountEngineTests()
    {
        _energy = _beverages.CreateChild("Energéticos");
        _sodas = _beverages.CreateChild("Refrigerantes");
        _energyDrink = Product.Create(Shop, "Energético 473ml", _energy.Id, UnitOfMeasure.Unit, Money.FromCents(1000), Gtin.Parse("7891000000014"));
        _soda = Product.Create(Shop, "Refrigerante 2L", _sodas.Id, UnitOfMeasure.Unit, Money.FromCents(1000), Gtin.Parse("7891000000021"));
    }

    // ---- reach -----------------------------------------------------------------------------

    [Fact]
    public void A_rule_on_Beverages_reaches_energy_drinks()
    {
        var quote = QuoteEnergyDrink(Percent("Bebidas -10%", 10, _beverages));

        Assert.Equal(900, quote.FinalPrice.Cents);
    }

    [Fact]
    public void A_rule_on_Energy_drinks_does_not_reach_sodas()
    {
        var rule = Percent("Energéticos -30%", 30, _energy);

        Assert.False(DiscountEngine.Quote(_soda, _sodas, [rule], Today, null).IsDiscounted);
        Assert.Equal(700, QuoteEnergyDrink(rule).FinalPrice.Cents);
    }

    [Fact]
    public void A_rule_on_one_product_leaves_the_others_alone()
    {
        var rule = DiscountRule.Create(Shop, "Só esse", DiscountType.Percentage, 2000, DiscountTarget.Product, _energyDrink.Id);

        Assert.False(DiscountEngine.Quote(_soda, _sodas, [rule], Today, null).IsDiscounted);
    }

    // ---- who wins ------------------------------------------------------------------------

    [Fact]
    public void A_rule_on_the_product_beats_any_rule_on_a_category()
    {
        var onProduct = DiscountRule.Create(Shop, "Produto -5%", DiscountType.Percentage, 500, DiscountTarget.Product, _energyDrink.Id);
        var onCategory = Percent("Energéticos -30%", 30, _energy);

        var quote = QuoteEnergyDrink(onCategory, onProduct);

        // Smaller, but aimed more precisely: the shop meant this product specifically.
        Assert.Equal(950, quote.FinalPrice.Cents);
        Assert.Equal("Produto -5%", Assert.Single(quote.Applied).RuleName);
    }

    [Fact]
    public void A_deeper_category_beats_a_shallower_one()
    {
        var quote = QuoteEnergyDrink(Percent("Bebidas -30%", 30, _beverages), Percent("Energéticos -10%", 10, _energy));

        Assert.Equal(900, quote.FinalPrice.Cents);
    }

    [Fact]
    public void Between_equally_specific_rules_the_higher_priority_wins()
    {
        var urgent = Percent("Prioritária -5%", 5, _energy);
        urgent.SetPriority(10);

        var quote = QuoteEnergyDrink(Percent("Comum -20%", 20, _energy), urgent);

        Assert.Equal(950, quote.FinalPrice.Cents);
    }

    [Fact]
    public void Between_equal_priorities_the_bigger_discount_wins()
    {
        var quote = QuoteEnergyDrink(Percent("-10%", 10, _energy), Percent("-25%", 25, _energy));

        Assert.Equal(750, quote.FinalPrice.Cents);
    }

    // ---- stacking ------------------------------------------------------------------------

    [Fact]
    public void Stackable_rules_apply_in_cascade_on_what_the_previous_left()
    {
        var quote = QuoteEnergyDrink(
            Stackable(Percent("Energéticos -10%", 10, _energy)),
            Stackable(Percent("Bebidas -10%", 10, _beverages)));

        // 1000 - 10% = 900, then 900 - 10% = 810; not 1000 - 20%.
        Assert.Equal(810, quote.FinalPrice.Cents);
        Assert.Equal(2, quote.Applied.Count);
    }

    [Fact]
    public void Stacked_rules_never_take_off_more_than_half()
    {
        var quote = QuoteEnergyDrink(
            Stackable(Percent("Energéticos -40%", 40, _energy)),
            Stackable(Percent("Bebidas -40%", 40, _beverages)));

        Assert.Equal(500, quote.FinalPrice.Cents);

        // What the screen lists still adds up to the price charged.
        Assert.Equal(quote.Discount, quote.Applied.Aggregate(Money.Zero, (sum, applied) => sum + applied.Amount));
    }

    [Fact]
    public void A_single_rule_is_applied_in_full_even_past_half()
    {
        var quote = QuoteEnergyDrink(Percent("Queima -70%", 70, _energy));

        Assert.Equal(300, quote.FinalPrice.Cents);
    }

    [Fact]
    public void A_winner_that_does_not_stack_stands_alone()
    {
        var quote = QuoteEnergyDrink(
            Percent("Energéticos -10%", 10, _energy),
            Stackable(Percent("Bebidas -20%", 20, _beverages)));

        Assert.Equal(900, quote.FinalPrice.Cents);
        Assert.Single(quote.Applied);
    }

    // ---- when ------------------------------------------------------------------------------

    [Fact]
    public void A_rule_runs_from_its_first_to_its_last_day_inclusive()
    {
        var rule = Percent("Semana", 10, _energy);
        rule.Schedule(Today, Today.AddDays(6), daysOfWeek: null);

        Assert.True(QuoteEnergyDrink(rule).IsDiscounted);
        Assert.False(DiscountEngine.Quote(_energyDrink, _energy, [rule], Today.AddDays(-1), null).IsDiscounted);
        Assert.True(DiscountEngine.Quote(_energyDrink, _energy, [rule], Today.AddDays(6), null).IsDiscounted);
        Assert.False(DiscountEngine.Quote(_energyDrink, _energy, [rule], Today.AddDays(7), null).IsDiscounted);
    }

    [Fact]
    public void A_rule_can_run_on_chosen_weekdays_only()
    {
        var tuesdays = Percent("Terça do energético", 15, _energy);
        tuesdays.Schedule(null, null, [DayOfWeek.Tuesday]);

        var tuesday = new DateOnly(2026, 9, 29);

        Assert.False(QuoteEnergyDrink(tuesdays).IsDiscounted);
        Assert.True(DiscountEngine.Quote(_energyDrink, _energy, [tuesdays], tuesday, null).IsDiscounted);
    }

    [Fact]
    public void A_switched_off_rule_does_nothing()
    {
        var rule = Percent("Pausada", 50, _energy);
        rule.Deactivate();

        Assert.False(QuoteEnergyDrink(rule).IsDiscounted);
    }

    // ---- expiry window ---------------------------------------------------------------------

    [Fact]
    public void An_expiry_window_reaches_units_that_expire_within_it()
    {
        var clearance = Percent("Vence em 3 dias -30%", 30, _energy);
        clearance.LimitToExpiringWithin(3);

        Assert.Equal(700, QuoteEnergyDrink(clearance, batchExpiry: Today.AddDays(3)).FinalPrice.Cents);
        Assert.Equal(700, QuoteEnergyDrink(clearance, batchExpiry: Today).FinalPrice.Cents);
    }

    [Fact]
    public void An_expiry_window_leaves_fresh_goods_and_goods_that_never_expire_alone()
    {
        var clearance = Percent("Vence em 3 dias -30%", 30, _energy);
        clearance.LimitToExpiringWithin(3);

        Assert.False(QuoteEnergyDrink(clearance, batchExpiry: Today.AddDays(4)).IsDiscounted);
        Assert.False(QuoteEnergyDrink(clearance, batchExpiry: null).IsDiscounted);
    }

    [Fact]
    public void An_expiry_window_never_prices_goods_already_expired()
    {
        var clearance = Percent("Vence em 3 dias -30%", 30, _energy);
        clearance.LimitToExpiringWithin(3);

        // Expired goods come off the shelf as a recorded loss; they are not sold cheap.
        Assert.False(QuoteEnergyDrink(clearance, batchExpiry: Today.AddDays(-1)).IsDiscounted);
    }

    // ---- amounts ---------------------------------------------------------------------------

    [Fact]
    public void A_fixed_price_sells_for_exactly_that()
    {
        var rule = DiscountRule.Create(Shop, "Leve por R$ 7,99", DiscountType.FixedPrice, 799, DiscountTarget.Category, _energy.Id);

        Assert.Equal(799, QuoteEnergyDrink(rule).FinalPrice.Cents);
    }

    [Fact]
    public void A_fixed_price_above_the_regular_price_is_not_a_discount()
    {
        var higher = DiscountRule.Create(Shop, "Preço fixo alto", DiscountType.FixedPrice, 1500, DiscountTarget.Category, _energy.Id);
        var modest = Percent("Bebidas -10%", 10, _beverages);

        // The more specific rule would take nothing off, so it does not win by default.
        Assert.Equal(900, QuoteEnergyDrink(higher, modest).FinalPrice.Cents);
    }

    [Fact]
    public void A_fixed_amount_never_takes_the_price_below_zero()
    {
        var rule = DiscountRule.Create(Shop, "R$ 50 off", DiscountType.FixedAmount, 5000, DiscountTarget.Category, _energy.Id);

        Assert.Equal(0, QuoteEnergyDrink(rule).FinalPrice.Cents);
    }

    [Fact]
    public void Another_shops_rules_are_ignored()
    {
        var foreign = DiscountRule.Create(Guid.CreateVersion7(), "Outra loja", DiscountType.Percentage, 5000, DiscountTarget.Category, _energy.Id);

        Assert.False(QuoteEnergyDrink(foreign).IsDiscounted);
    }

    // ---- validation ------------------------------------------------------------------------

    [Theory]
    [InlineData(DiscountType.Percentage, 0)]
    [InlineData(DiscountType.Percentage, 10_001)]
    [InlineData(DiscountType.FixedAmount, 0)]
    [InlineData(DiscountType.FixedPrice, -1)]
    public void A_discount_needs_a_value_that_makes_sense(DiscountType type, long value)
    {
        DomainAssert.Breaks(
            DomainErrors.DiscountValueInvalid,
            () => DiscountRule.Create(Shop, "Regra", type, value, DiscountTarget.Category, _energy.Id));
    }

    [Fact]
    public void A_rule_cannot_end_before_it_starts()
    {
        var rule = Percent("Regra", 10, _energy);

        DomainAssert.Breaks(DomainErrors.DiscountPeriodInvalid, () => rule.Schedule(Today, Today.AddDays(-1), null));
    }

    [Fact]
    public void An_expiry_window_runs_from_zero_to_a_year()
    {
        var rule = Percent("Regra", 10, _energy);

        DomainAssert.Breaks(DomainErrors.DiscountExpiryWindowInvalid, () => rule.LimitToExpiringWithin(-1));
        DomainAssert.Breaks(DomainErrors.DiscountExpiryWindowInvalid, () => rule.LimitToExpiringWithin(366));
    }

    private PriceQuote QuoteEnergyDrink(params DiscountRule[] rules) =>
        DiscountEngine.Quote(_energyDrink, _energy, rules, Today, batchExpiry: null);

    private PriceQuote QuoteEnergyDrink(DiscountRule rule, DateOnly? batchExpiry) =>
        DiscountEngine.Quote(_energyDrink, _energy, [rule], Today, batchExpiry);

    private static DiscountRule Percent(string name, int percent, Category category) =>
        DiscountRule.Create(Shop, name, DiscountType.Percentage, percent * 100, DiscountTarget.Category, category.Id);

    private static DiscountRule Stackable(DiscountRule rule)
    {
        rule.SetStackable(true);
        return rule;
    }
}
