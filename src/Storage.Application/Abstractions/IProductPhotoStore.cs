using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Abstractions;

/// <summary>
/// The photos of barcodes, shared by every shop. Not scoped to a tenant: see
/// <see cref="ProductPhoto"/>.
/// </summary>
public interface IProductPhotoStore
{
    /// <summary>
    /// Asks for the photo of each code that was never asked for. A code already known -
    /// ready, missing or waiting - is left exactly as it is.
    /// </summary>
    Task RequestAsync(IReadOnlyCollection<Gtin> gtins, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for the photo of every code on any shop's products that was never asked for: the
    /// products registered before photos existed, or while a request failed.
    /// </summary>
    Task RequestAllCatalogedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the code that has waited longest among those due, and pushes its next attempt
    /// <paramref name="lease"/> ahead, so another worker - or this one, after a crash - only
    /// takes it once that time has passed. Null when nothing is due.
    /// </summary>
    Task<ProductPhoto?> ClaimNextAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Records the outcome of an attempt, with the picture when one was made.</summary>
    Task SaveAsync(ProductPhoto photo, PhotoFile? file, CancellationToken cancellationToken = default);

    /// <summary>The ready photos among <paramref name="gtins"/>, by code.</summary>
    Task<IReadOnlyDictionary<Gtin, ProductPhoto>> FindReadyAsync(
        IReadOnlyCollection<Gtin> gtins,
        CancellationToken cancellationToken = default);

    Task<PhotoFile?> OpenAsync(Gtin gtin, CancellationToken cancellationToken = default);
}

/// <summary>The stored picture itself.</summary>
public sealed record PhotoFile(string Version, string ContentType, byte[] Content);
