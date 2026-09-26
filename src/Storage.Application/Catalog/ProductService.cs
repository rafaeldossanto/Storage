using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Catalog;

public sealed class ProductService(
    IProductRepository products,
    ICategoryRepository categories,
    ITenantContext tenant)
{
    public async Task<ProductDto> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await RequireAsync(id, cancellationToken)).ToDto();

    /// <summary>
    /// The product a scanned code belongs to.
    /// </summary>
    /// <remarks>
    /// A not-found here is not an error from the shopkeeper's point of view: it is the signal
    /// the registration screen waits for to open a form already filled with the code.
    /// </remarks>
    public async Task<ProductDto> FindByBarcodeAsync(
        string barcode,
        CancellationToken cancellationToken = default)
    {
        var gtin = ParseBarcode(barcode);

        var product = await products.FindByGtinAsync(gtin, cancellationToken)
            ?? throw UseCaseException.NotFound(
                ErrorCodes.ProductNotFound,
                $"No product answers to barcode {gtin.ToDisplay()}.");

        return product.ToDto();
    }

    public async Task<Paged<ProductDto>> SearchAsync(
        string term,
        PageRequest page,
        CancellationToken cancellationToken = default) =>
        (await products.SearchAsync(term, page, cancellationToken)).Map(product => product.ToDto());

    public async Task<Paged<ProductDto>> ListByCategoryAsync(
        Guid categoryId,
        bool includeDescendants,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        var category = await RequireCategoryAsync(categoryId, cancellationToken);

        return (await products.ListByCategoryAsync(category, includeDescendants, page, cancellationToken))
            .Map(product => product.ToDto());
    }

    public async Task<ProductDto> CreateAsync(
        CreateProductRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var gtin = ParseBarcode(request.Barcode);
        await RequireCategoryAsync(request.CategoryId, cancellationToken);
        await EnsureBarcodeIsFreeAsync(gtin, cancellationToken);

        var product = Product.Create(
            tenant.TenantId,
            request.Name,
            request.CategoryId,
            request.BaseUnit,
            Money.FromCents(request.SalePriceCents),
            gtin);

        product.SetMinimumStock(request.MinimumStock);
        product.SetExpiryTracking(request.TracksExpiry);

        await products.AddAsync(product, cancellationToken);
        return product.ToDto();
    }

    public async Task<ProductDto> UpdateAsync(
        Guid id,
        UpdateProductRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var product = await RequireAsync(id, cancellationToken);

        if (request.CategoryId != product.CategoryId)
        {
            await RequireCategoryAsync(request.CategoryId, cancellationToken);
        }

        product.Rename(request.Name);
        product.MoveToCategory(request.CategoryId);
        product.ChangePrice(Money.FromCents(request.SalePriceCents));
        product.SetMinimumStock(request.MinimumStock);
        product.SetExpiryTracking(request.TracksExpiry);

        await products.UpdateAsync(product, cancellationToken);
        return product.ToDto();
    }

    public async Task<ProductDto> AddPackagingAsync(
        Guid id,
        AddPackagingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var product = await RequireAsync(id, cancellationToken);
        var gtin = ParseBarcode(request.Barcode);

        // Checked across the whole shop, not just this product: a code already on another
        // product would make every future scan ambiguous.
        await EnsureBarcodeIsFreeAsync(gtin, cancellationToken);

        product.AddPackaging(gtin, request.Name, request.ConversionFactor);

        await products.UpdateAsync(product, cancellationToken);
        return product.ToDto();
    }

    public async Task<ProductDto> RemovePackagingAsync(
        Guid id,
        Guid packagingId,
        CancellationToken cancellationToken = default)
    {
        var product = await RequireAsync(id, cancellationToken);
        product.RemovePackaging(packagingId);

        await products.UpdateAsync(product, cancellationToken);
        return product.ToDto();
    }

    public async Task<ProductDto> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var product = await RequireAsync(id, cancellationToken);
        product.Activate();

        await products.UpdateAsync(product, cancellationToken);
        return product.ToDto();
    }

    public async Task<ProductDto> DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var product = await RequireAsync(id, cancellationToken);
        product.Deactivate();

        await products.UpdateAsync(product, cancellationToken);
        return product.ToDto();
    }

    private static Gtin ParseBarcode(string? barcode) =>
        Gtin.TryParse(barcode, out var gtin)
            ? gtin
            : throw UseCaseException.Invalid(
                ErrorCodes.BarcodeInvalid,
                $"'{barcode}' is not a valid barcode.");

    /// <remarks>
    /// This check gives a clear answer in the common case. It is not what guarantees
    /// uniqueness - two requests can pass it at the same moment - which is why the unique
    /// index exists and the repository translates its violation into the same error.
    /// </remarks>
    private async Task EnsureBarcodeIsFreeAsync(Gtin gtin, CancellationToken cancellationToken)
    {
        if (await products.FindByGtinAsync(gtin, cancellationToken) is not null)
        {
            throw UseCaseException.Conflict(
                ErrorCodes.BarcodeTaken,
                $"Barcode {gtin.ToDisplay()} already belongs to a product.");
        }
    }

    private async Task<Product> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await products.FindAsync(id, cancellationToken)
        ?? throw UseCaseException.NotFound(ErrorCodes.ProductNotFound, $"Product {id} does not exist.");

    private async Task<Category> RequireCategoryAsync(Guid id, CancellationToken cancellationToken) =>
        await categories.FindAsync(id, cancellationToken)
        ?? throw UseCaseException.NotFound(ErrorCodes.CategoryNotFound, $"Category {id} does not exist.");
}
