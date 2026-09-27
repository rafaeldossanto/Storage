using Storage.Domain.Catalog;

namespace Storage.Application.Catalog;

// Money crosses the API as whole cents (long). A JSON number is a double in the browser,
// and 8.99 cannot be represented exactly there; 899 can. The front end formats it.

public sealed record CategoryDto(
    Guid Id,
    string Name,
    Guid? ParentId,
    int Depth,
    bool Active);

public sealed record CategoryTreeNode(
    Guid Id,
    string Name,
    int Depth,
    bool Active,
    IReadOnlyList<CategoryTreeNode> Children);

public sealed record CreateCategoryRequest(string Name, Guid? ParentId);

public sealed record RenameCategoryRequest(string Name);

/// <summary>A null parent moves the category to the root.</summary>
public sealed record MoveCategoryRequest(Guid? ParentId);

public sealed record PackagingDto(
    Guid Id,
    string Gtin,
    string DisplayGtin,
    string? Name,
    int ConversionFactor,
    bool IsDefault,
    bool IsInternalCode);

/// <param name="Photo">
/// Null until the photo of one of the product's codes is ready - just registered, not
/// found anywhere, or a code the shop minted itself. The screen shows the monogram then.
/// </param>
public sealed record ProductDto(
    Guid Id,
    string Name,
    Guid CategoryId,
    UnitOfMeasure BaseUnit,
    long SalePriceCents,
    int MinimumStock,
    bool TracksExpiry,
    bool Active,
    IReadOnlyList<PackagingDto> Packagings,
    ProductPhotoDto? Photo,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="Url">
/// Relative to the API: <c>/api/product-photos/{gtin}?v={version}</c>. The version changes
/// with the picture, so the address can be cached for good.
/// </param>
/// <param name="Source">Who to credit, as the licence asks: "Open Food Facts".</param>
public sealed record ProductPhotoDto(string Url, string Source, string SourcePage, string License)
{
    public const string Route = "/api/product-photos";
}

public sealed record CreateProductRequest(
    string Name,
    Guid CategoryId,
    string Barcode,
    long SalePriceCents,
    UnitOfMeasure BaseUnit = UnitOfMeasure.Unit,
    int MinimumStock = 0,
    bool TracksExpiry = true);

public sealed record UpdateProductRequest(
    string Name,
    Guid CategoryId,
    long SalePriceCents,
    int MinimumStock,
    bool TracksExpiry);

public sealed record AddPackagingRequest(string Barcode, string? Name, int ConversionFactor);
