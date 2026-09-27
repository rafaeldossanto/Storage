using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Catalog;

internal static class CatalogMappings
{
    public static CategoryDto ToDto(this Category category) => new(
        category.Id,
        category.Name,
        category.ParentId,
        category.Depth,
        category.Active);

    /// <param name="photos">The ready photos of the product's codes, from <see cref="PhotoCodes"/>.</param>
    public static ProductDto ToDto(this Product product, IReadOnlyDictionary<Gtin, ProductPhoto> photos)
    {
        var packagings = InDisplayOrder(product);

        return new ProductDto(
            product.Id,
            product.Name,
            product.CategoryId,
            product.BaseUnit,
            product.SalePrice.Cents,
            product.MinimumStock,
            product.TracksExpiry,
            product.Active,
            packagings.Select(ToDto).ToArray(),
            // The photo of the can the product is counted in, or else of any of its packs.
            packagings
                .Select(packaging => photos.GetValueOrDefault(packaging.Gtin))
                .FirstOrDefault(photo => photo is not null)
                ?.ToDto(),
            product.CreatedAt,
            product.UpdatedAt);
    }

    /// <summary>The codes of the products that can have a photo: all but the shop's own.</summary>
    public static Gtin[] PhotoCodes(IEnumerable<Product> products) =>
        products
            .SelectMany(product => product.Packagings)
            .Select(packaging => packaging.Gtin)
            .Where(gtin => !gtin.IsInternal)
            .Distinct()
            .ToArray();

    public static PackagingDto ToDto(this PackagingUnit packaging) => new(
        packaging.Id,
        packaging.Gtin.Value,
        packaging.Gtin.ToDisplay(),
        packaging.Name,
        packaging.ConversionFactor,
        packaging.IsDefault,
        packaging.IsInternalCode);

    private static ProductPhotoDto ToDto(this ProductPhoto photo) => new(
        $"{ProductPhotoDto.Route}/{photo.Gtin.Value}?v={photo.Version}",
        photo.Source!,
        photo.SourcePage!,
        photo.License!);

    // The base unit first, so a screen can show "the" barcode without searching for it.
    private static PackagingUnit[] InDisplayOrder(Product product) =>
        product.Packagings
            .OrderByDescending(packaging => packaging.IsDefault)
            .ThenBy(packaging => packaging.ConversionFactor)
            .ToArray();
}
