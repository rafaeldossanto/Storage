namespace Storage.Infrastructure.Photos;

/// <summary>Where product photos come from and how they are made. Filled in by the API from configuration.</summary>
public sealed record ProductPhotoOptions
{
    /// <summary>
    /// The Open Food Facts family, asked in this order: food and drink first - most of a
    /// market - then hygiene and cleaning, then everything else.
    /// </summary>
    public IReadOnlyList<PhotoSite> Sites { get; init; } =
    [
        new("Open Food Facts", new Uri("https://world.openfoodfacts.org/")),
        new("Open Beauty Facts", new Uri("https://world.openbeautyfacts.org/")),
        new("Open Products Facts", new Uri("https://world.openproductsfacts.org/")),
    ];

    /// <summary>Sent with every request, as the Open Food Facts API asks: who is calling.</summary>
    public string UserAgent { get; init; } = "Storage/1.0 (+https://github.com/rafaeldossanto/Storage)";

    /// <summary>
    /// The ISNet model that cuts any package out of any background. Left out, or not on
    /// disk, photos are cut out only when shot on a plain backdrop.
    /// </summary>
    public string? CutoutModelPath { get; init; }
}

/// <param name="BaseAddress">Ends with a slash, so relative paths append to it.</param>
public sealed record PhotoSite(string Name, Uri BaseAddress);
