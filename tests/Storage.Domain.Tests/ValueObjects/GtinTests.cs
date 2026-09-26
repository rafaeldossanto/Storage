using Storage.Domain.ValueObjects;

namespace Storage.Domain.Tests.ValueObjects;

public class GtinTests
{
    // Real-world shapes: an EAN-13 on a Brazilian product (789 prefix), the GS1 sample
    // EAN-8, a DUN-14 for the outer case, and an in-store code from the 2 range.
    private const string Ean13 = "7891000000014";
    private const string Ean8 = "40063812";
    private const string Dun14 = "17891000000011";
    private const string InStoreCode = "2001234005678";

    [Theory]
    [InlineData(Ean13)]
    [InlineData(Ean8)]
    [InlineData(Dun14)]
    [InlineData(InStoreCode)]
    public void TryParse_accepts_every_valid_length(string input)
    {
        Assert.True(Gtin.TryParse(input, out var gtin));
        Assert.Equal(Gtin.NormalizedLength, gtin.Value.Length);
    }

    [Fact]
    public void Shorter_codes_are_padded_to_fourteen_digits()
    {
        var gtin = Gtin.Parse(Ean13);

        Assert.Equal("07891000000014", gtin.Value);
    }

    [Theory]
    [InlineData("7891000000015")] // check digit off by one
    [InlineData("789100000001")]  // 12 digits, but not a valid UPC either
    [InlineData("1234")]          // too short
    [InlineData("789100000001456789")] // too long
    [InlineData("789100000001X")] // not a number
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_rejects_anything_that_is_not_a_real_barcode(string? input)
    {
        Assert.False(Gtin.TryParse(input, out _));
    }

    [Fact]
    public void Parse_throws_on_an_invalid_code()
    {
        Assert.Throws<ArgumentException>(() => Gtin.Parse("7891000000015"));
    }

    [Fact]
    public void Separators_typed_by_hand_are_ignored()
    {
        Assert.True(Gtin.TryParse(" 789 1000 000014 ", out var gtin));
        Assert.Equal(Gtin.Parse(Ean13), gtin);
    }

    [Fact]
    public void Codes_in_the_restricted_range_are_flagged_as_internal()
    {
        Assert.True(Gtin.Parse(InStoreCode).IsInternal);
    }

    [Fact]
    public void Manufacturer_codes_are_not_internal()
    {
        Assert.False(Gtin.Parse(Ean13).IsInternal);
        Assert.False(Gtin.Parse(Dun14).IsInternal);
    }

    [Fact]
    public void CalculateCheckDigit_produces_a_code_that_parses_back()
    {
        var body = "789100000001";

        var checkDigit = Gtin.CalculateCheckDigit(body);

        Assert.Equal(4, checkDigit);
        Assert.True(Gtin.TryParse(body + checkDigit, out _));
    }

    [Theory]
    [InlineData(Ean13, Ean13)]
    [InlineData(Ean8, Ean8)]
    [InlineData(Dun14, Dun14)]
    public void ToDisplay_gives_back_the_printed_form(string input, string expected)
    {
        Assert.Equal(expected, Gtin.Parse(input).ToDisplay());
    }

    [Fact]
    public void The_same_number_in_different_lengths_is_the_same_value()
    {
        Assert.Equal(Gtin.Parse(Ean13), Gtin.Parse("0" + Ean13));
    }
}
