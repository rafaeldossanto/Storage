using Storage.Domain.Catalog;

namespace Storage.Application.Catalog;

internal static class CatalogMappings
{
    public static CategoryDto ToDto(this Category category) => new(
        category.Id,
        category.Name,
        category.ParentId,
        category.Depth,
        category.Active);

    public static ProductDto ToDto(this Product product) => new(
        product.Id,
        product.Name,
        product.CategoryId,
        product.BaseUnit,
        product.SalePrice.Cents,
        product.MinimumStock,
        product.TracksExpiry,
        product.Active,
        // The base unit first, so a screen can show "the" barcode without searching for it.
        product.Packagings
            .OrderByDescending(packaging => packaging.IsDefault)
            .ThenBy(packaging => packaging.ConversionFactor)
            .Select(ToDto)
            .ToArray(),
        product.CreatedAt,
        product.UpdatedAt);

    public static PackagingDto ToDto(this PackagingUnit packaging) => new(
        packaging.Id,
        packaging.Gtin.Value,
        packaging.Gtin.ToDisplay(),
        packaging.Name,
        packaging.ConversionFactor,
        packaging.IsDefault,
        packaging.IsInternalCode);
}
