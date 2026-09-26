using Storage.Application.Catalog;
using Storage.Application.Errors;
using Storage.Application.Tests.Fakes;
using Storage.Domain.Catalog;
using Storage.Domain.Common;

namespace Storage.Application.Tests.Catalog;

public sealed class ProductServiceTests
{
    private const string CanBarcode = "7891000000014";
    private const string PackBarcode = "17891000000011";
    private const string OtherBarcode = "7891000000021";

    private readonly InMemoryCategoryRepository _categories;
    private readonly ProductService _service;
    private readonly Category _beverages;

    public ProductServiceTests()
    {
        var tenant = new FixedTenant(Guid.CreateVersion7());
        _categories = new InMemoryCategoryRepository(tenant);
        _service = new ProductService(new InMemoryProductRepository(tenant), _categories, tenant);

        _beverages = Category.CreateRoot(tenant.TenantId, "Bebidas");
        _categories.Seed(_beverages);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_price_goes_in_and_comes_out_as_cents()
    {
        var product = await CreateAsync(CanBarcode, priceCents: 899);

        Assert.Equal(899, product.SalePriceCents);
    }

    [Fact]
    public async Task A_barcode_with_a_wrong_check_digit_is_refused_as_invalid()
    {
        await Refused.WithAsync(
            ErrorKind.Invalid,
            ErrorCodes.BarcodeInvalid,
            () => CreateAsync("7891000000015"));
    }

    [Fact]
    public async Task A_product_in_a_category_that_does_not_exist_is_not_found()
    {
        await Refused.WithAsync(
            ErrorKind.NotFound,
            ErrorCodes.CategoryNotFound,
            () => _service.CreateAsync(
                new CreateProductRequest("Energético 473ml", Guid.CreateVersion7(), CanBarcode, 899),
                Token));
    }

    [Fact]
    public async Task A_barcode_already_in_use_is_a_conflict()
    {
        await CreateAsync(CanBarcode);

        await Refused.WithAsync(
            ErrorKind.Conflict,
            ErrorCodes.BarcodeTaken,
            () => CreateAsync(CanBarcode, name: "Outro produto"));
    }

    [Fact]
    public async Task An_unknown_barcode_is_not_found_so_the_screen_can_open_registration()
    {
        await Refused.WithAsync(
            ErrorKind.NotFound,
            ErrorCodes.ProductNotFound,
            () => _service.FindByBarcodeAsync(OtherBarcode, Token));
    }

    [Theory]
    [InlineData("7891000000014")]
    [InlineData("07891000000014")]
    [InlineData(" 789 1000 000014 ")]
    public async Task Any_printed_form_of_the_barcode_finds_the_product(string scanned)
    {
        var created = await CreateAsync(CanBarcode);

        var found = await _service.FindByBarcodeAsync(scanned, Token);

        Assert.Equal(created.Id, found.Id);
    }

    [Fact]
    public async Task Scanning_the_pack_finds_the_same_product()
    {
        var created = await CreateAsync(CanBarcode);
        await _service.AddPackagingAsync(
            created.Id, new AddPackagingRequest(PackBarcode, "Fardo 12", 12), Token);

        var found = await _service.FindByBarcodeAsync(PackBarcode, Token);

        Assert.Equal(created.Id, found.Id);
    }

    [Fact]
    public async Task A_pack_barcode_that_belongs_to_another_product_is_a_conflict()
    {
        await CreateAsync(CanBarcode);
        var other = await CreateAsync(OtherBarcode, name: "Refrigerante 350ml");

        await Refused.WithAsync(
            ErrorKind.Conflict,
            ErrorCodes.BarcodeTaken,
            () => _service.AddPackagingAsync(
                other.Id, new AddPackagingRequest(CanBarcode, "Fardo", 6), Token));
    }

    [Fact]
    public async Task The_base_unit_packaging_comes_first()
    {
        var created = await CreateAsync(CanBarcode);

        var withPack = await _service.AddPackagingAsync(
            created.Id, new AddPackagingRequest(PackBarcode, "Fardo 12", 12), Token);

        Assert.True(withPack.Packagings[0].IsDefault);
        Assert.Equal("7891000000014", withPack.Packagings[0].DisplayGtin);
    }

    [Fact]
    public async Task Moving_a_product_to_a_category_that_does_not_exist_is_not_found()
    {
        var created = await CreateAsync(CanBarcode);

        await Refused.WithAsync(
            ErrorKind.NotFound,
            ErrorCodes.CategoryNotFound,
            () => _service.UpdateAsync(
                created.Id,
                new UpdateProductRequest("Energético 473ml", Guid.CreateVersion7(), 899, 0, true),
                Token));
    }

    [Fact]
    public async Task Domain_rules_still_reach_the_caller_with_their_code()
    {
        var refusal = await Assert.ThrowsAsync<DomainException>(() => CreateAsync(CanBarcode, priceCents: -1));

        Assert.Equal(DomainErrors.ProductPriceNegative, refusal.Code);
    }

    private Task<ProductDto> CreateAsync(
        string barcode,
        long priceCents = 899,
        string name = "Energético 473ml") =>
        _service.CreateAsync(
            new CreateProductRequest(name, _beverages.Id, barcode, priceCents),
            Token);
}
