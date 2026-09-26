using System.Globalization;
using Bipe.Domain.ValueObjects;

namespace Bipe.Domain.Tests.ValueObjects;

public class MoneyTests
{
    [Theory]
    [InlineData(8.99, 899)]
    [InlineData(0.01, 1)]
    [InlineData(0, 0)]
    [InlineData(-2.50, -250)]
    // Half a cent rounds up, the way a price tag does - bankers rounding would send
    // 0.005 down to zero and make the shopkeeper lose the cent.
    [InlineData(0.005, 1)]
    [InlineData(8.995, 900)]
    public void FromDecimal_rounds_to_the_nearest_cent(decimal amount, long expectedCents)
    {
        Assert.Equal(expectedCents, Money.FromDecimal(amount).Cents);
    }

    [Fact]
    public void Percentage_rounds_the_discount_to_a_whole_cent()
    {
        var price = Money.FromDecimal(8.99m);

        // 30% of 899 cents is 269.7
        Assert.Equal(270, price.Percentage(30).Cents);
    }

    [Fact]
    public void Times_handles_fractional_quantities()
    {
        var pricePerKilo = Money.FromDecimal(42.90m);

        Assert.Equal(1502, pricePerKilo.Times(0.350m).Cents);
    }

    [Fact]
    public void Allocate_spreads_the_remainder_without_losing_cents()
    {
        var pieces = Money.FromCents(100).Allocate(3);

        Assert.Equal([34, 33, 33], pieces.Select(p => p.Cents));
        Assert.Equal(100, pieces.Sum(p => p.Cents));
    }

    [Fact]
    public void Allocate_keeps_the_sign_when_the_amount_is_negative()
    {
        var pieces = Money.FromCents(-100).Allocate(3);

        Assert.Equal([-34, -33, -33], pieces.Select(p => p.Cents));
        Assert.Equal(-100, pieces.Sum(p => p.Cents));
    }

    [Fact]
    public void AllocateByWeights_adds_up_to_the_original_amount()
    {
        // A 10 unit line drawn from two batches, 7 units and 3 units.
        var pieces = Money.FromCents(1000).AllocateByWeights([7m, 3m]);

        Assert.Equal(1000, pieces.Sum(p => p.Cents));
        Assert.Equal(700, pieces[0].Cents);
        Assert.Equal(300, pieces[1].Cents);
    }

    [Fact]
    public void AllocateByWeights_gives_leftover_cents_to_the_heaviest_weight()
    {
        var pieces = Money.FromCents(100).AllocateByWeights([1m, 1m, 1m]);

        Assert.Equal(100, pieces.Sum(p => p.Cents));
        Assert.Equal(34, pieces.Max(p => p.Cents));
    }

    [Fact]
    public void Arithmetic_stays_in_cents()
    {
        var a = Money.FromDecimal(10.10m);
        var b = Money.FromDecimal(0.05m);

        Assert.Equal(1015, (a + b).Cents);
        Assert.Equal(1005, (a - b).Cents);
        Assert.Equal(3030, (a * 3).Cents);
        Assert.True(a > b);
        Assert.True(b < a);
    }

    [Fact]
    public void Summing_many_small_amounts_never_drifts()
    {
        // The float trap: 0.10 added a thousand times is not 100.00 in binary floating point.
        var total = Enumerable.Range(0, 1000)
            .Aggregate(Money.Zero, (acc, _) => acc + Money.FromDecimal(0.10m));

        Assert.Equal(10_000, total.Cents);
        Assert.Equal(100.00m, total.Amount);
    }

    [Fact]
    public void ToString_uses_the_ambient_culture()
    {
        var formatted = Money.FromDecimal(8.99m).ToString(new CultureInfo("pt-BR"));

        Assert.StartsWith("R$", formatted, StringComparison.Ordinal);
        Assert.Contains("8,99", formatted, StringComparison.Ordinal);
    }
}
