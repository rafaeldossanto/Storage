using Storage.Domain.ValueObjects;

namespace Storage.Application.Abstractions;

/// <summary>Somewhere to find the photo of a barcode: Open Food Facts and its sister databases.</summary>
public interface IProductPhotoSource
{
    /// <summary>
    /// The photo of the front of the package, as the source has it. Null when no source
    /// knows the code or has a photo of it; throws when a source could not be asked, so the
    /// attempt is retried instead of the code being written off.
    /// </summary>
    Task<SourcePhoto?> FindAsync(Gtin gtin, CancellationToken cancellationToken = default);
}

/// <param name="Source">The name the licence asks to be credited: "Open Food Facts".</param>
/// <param name="SourcePage">The product's page at the source.</param>
public sealed record SourcePhoto(byte[] Content, string Source, string SourcePage, string License);
