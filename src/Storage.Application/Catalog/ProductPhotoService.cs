using System.Security.Cryptography;
using Storage.Application.Abstractions;
using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Catalog;

public enum PhotoAttemptOutcome
{
    Stored,
    Missing,
    Failed,
}

/// <summary>What happened to one code, for the worker to log.</summary>
public sealed record PhotoAttempt(Gtin Gtin, PhotoAttemptOutcome Outcome, string? Source = null, Exception? Error = null);

/// <summary>
/// Finds the photo of a barcode, has it cut out on white and stores it - one code at a
/// time, in the background, so registering a product never waits for any of it.
/// </summary>
public sealed class ProductPhotoService(
    IProductPhotoStore store,
    IProductPhotoSource source,
    IPackshotStudio studio,
    TimeProvider clock)
{
    /// <summary>
    /// How long a claimed code is left alone. Far longer than an attempt takes; it only
    /// matters when a worker dies halfway, and then the code is simply picked up again.
    /// </summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>Works on the next due code. Null when none is due.</summary>
    public async Task<PhotoAttempt?> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        var photo = await store.ClaimNextAsync(clock.GetUtcNow(), Lease, cancellationToken);

        if (photo is null)
        {
            return null;
        }

        try
        {
            var found = await source.FindAsync(photo.Gtin, cancellationToken);

            if (found is null)
            {
                photo.MarkMissing(clock.GetUtcNow());
                await store.SaveAsync(photo, file: null, cancellationToken);
                return new PhotoAttempt(photo.Gtin, PhotoAttemptOutcome.Missing);
            }

            var packshot = await studio.MakePackshotAsync(found.Content, cancellationToken);
            var version = VersionOf(packshot.Content);

            photo.Store(version, found.Source, found.SourcePage, found.License, clock.GetUtcNow());
            await store.SaveAsync(photo, new PhotoFile(version, packshot.ContentType, packshot.Content), cancellationToken);

            return new PhotoAttempt(photo.Gtin, PhotoAttemptOutcome.Stored, found.Source);
        }
        catch (UnusablePhotoException error)
        {
            // Trying again would read the same broken file: as good as no photo.
            photo.MarkMissing(clock.GetUtcNow());
            await store.SaveAsync(photo, file: null, cancellationToken);
            return new PhotoAttempt(photo.Gtin, PhotoAttemptOutcome.Missing, Error: error);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // A source down, a timeout, a hiccup: nothing is known about the code yet.
            photo.RecordFailure(clock.GetUtcNow());
            await store.SaveAsync(photo, file: null, cancellationToken);
            return new PhotoAttempt(photo.Gtin, PhotoAttemptOutcome.Failed, Error: error);
        }
    }

    /// <summary>The picture behind a photo address. Null for a code that has none, or is not a code.</summary>
    public Task<PhotoFile?> OpenAsync(string gtin, CancellationToken cancellationToken = default) =>
        Gtin.TryParse(gtin, out var parsed)
            ? store.OpenAsync(parsed, cancellationToken)
            : Task.FromResult<PhotoFile?>(null);

    /// <summary>
    /// A fingerprint of the picture. Short, since it travels in every product's photo
    /// address; 16 hex digits of SHA-256 leave no realistic chance of two pictures of one
    /// code sharing it.
    /// </summary>
    private static string VersionOf(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content))[..16];
}
