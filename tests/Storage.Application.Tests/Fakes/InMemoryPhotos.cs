using Storage.Application.Abstractions;
using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Tests.Fakes;

/// <summary>The photo store in memory, with the same rules as the MongoDB one.</summary>
internal sealed class InMemoryProductPhotoStore(TimeProvider clock) : IProductPhotoStore
{
    private readonly Dictionary<Gtin, ProductPhoto> _photos = [];
    private readonly Dictionary<Gtin, PhotoFile> _files = [];

    public IReadOnlyDictionary<Gtin, ProductPhoto> Photos => _photos;

    public Task RequestAsync(IReadOnlyCollection<Gtin> gtins, CancellationToken cancellationToken = default)
    {
        foreach (var gtin in gtins.Where(gtin => !gtin.IsInternal))
        {
            _photos.TryAdd(gtin, ProductPhoto.Request(gtin, clock.GetUtcNow()));
        }

        return Task.CompletedTask;
    }

    public Task RequestAllCatalogedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ProductPhoto?> ClaimNextAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var due = _photos.Values
            .Where(photo => photo.IsDue(now))
            .OrderBy(photo => photo.NextAttemptAt)
            .FirstOrDefault();

        // The lease, as the real store does it: the next attempt pushed ahead.
        if (due is not null)
        {
            SetNextAttempt(due, now + lease);
        }

        return Task.FromResult(due);
    }

    public Task SaveAsync(ProductPhoto photo, PhotoFile? file, CancellationToken cancellationToken = default)
    {
        if (file is not null)
        {
            _files[photo.Gtin] = file;
        }

        _photos[photo.Gtin] = photo;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<Gtin, ProductPhoto>> FindReadyAsync(
        IReadOnlyCollection<Gtin> gtins,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Gtin, ProductPhoto>>(
            gtins
                .Where(gtin => _photos.TryGetValue(gtin, out var photo) && photo.Status == ProductPhotoStatus.Ready)
                .ToDictionary(gtin => gtin, gtin => _photos[gtin]));

    public Task<PhotoFile?> OpenAsync(Gtin gtin, CancellationToken cancellationToken = default) =>
        Task.FromResult(_files.GetValueOrDefault(gtin));

    /// <summary>Reaches the private setter the way the database driver does.</summary>
    private static void SetNextAttempt(ProductPhoto photo, DateTimeOffset at) =>
        typeof(ProductPhoto).GetProperty(nameof(ProductPhoto.NextAttemptAt))!.SetValue(photo, at);
}

/// <summary>A source that knows the codes it was given, and fails for the ones it was told to.</summary>
internal sealed class FakePhotoSource : IProductPhotoSource
{
    private readonly Dictionary<Gtin, SourcePhoto> _known = [];
    private readonly HashSet<Gtin> _failing = [];

    public List<Gtin> Asked { get; } = [];

    public void Knows(Gtin gtin, byte[] content) =>
        _known[gtin] = new SourcePhoto(content, "Open Food Facts", $"https://world.openfoodfacts.org/product/{gtin.ToDisplay()}", "CC BY-SA 3.0");

    public void FailsFor(Gtin gtin) => _failing.Add(gtin);

    public Task<SourcePhoto?> FindAsync(Gtin gtin, CancellationToken cancellationToken = default)
    {
        Asked.Add(gtin);

        return _failing.Contains(gtin)
            ? throw new HttpRequestException("The source is down.")
            : Task.FromResult(_known.GetValueOrDefault(gtin));
    }
}

/// <summary>
/// "Cuts out" by reversing the bytes, so a test can tell the stored picture came through
/// the studio. Bytes starting with 0 are an unreadable photo.
/// </summary>
internal sealed class FakeStudio : IPackshotStudio
{
    public Task<Packshot> MakePackshotAsync(byte[] photo, CancellationToken cancellationToken = default) =>
        photo.Length > 0 && photo[0] == 0
            ? throw new UnusablePhotoException("Not an image.")
            : Task.FromResult(new Packshot("image/webp", photo.Reverse().ToArray()));
}
