using Storage.Domain.ValueObjects;

namespace Storage.Domain.Catalog;

public enum ProductPhotoStatus
{
    /// <summary>Asked for and not found yet: waiting for its turn, or for a retry.</summary>
    Pending,

    /// <summary>Found, cut out and stored: the product shows it.</summary>
    Ready,

    /// <summary>
    /// No source has a photo for this code. Looked for again from time to time, since the
    /// open databases keep growing.
    /// </summary>
    Missing,
}

/// <summary>
/// The picture of a barcode: the can of Coca-Cola on a white background that every shop
/// selling that can sees next to its name.
/// </summary>
/// <remarks>
/// Not <see cref="Common.ITenantScoped"/> on purpose. A GS1 code names the same product in
/// every shop, so its photo is looked up, cut out and stored once for the whole platform,
/// and a fix to a bad photo reaches every shop at once. Codes the shop minted itself
/// (<see cref="Gtin.IsInternal"/>) mean something different in each shop and never get one.
/// </remarks>
public sealed class ProductPhoto
{
    /// <summary>How long a code no source knew waits before it is looked up again.</summary>
    public static readonly TimeSpan RecheckMissingAfter = TimeSpan.FromDays(30);

    /// <summary>
    /// Waits after each failed attempt in a row - a source down, a timeout - growing so a
    /// long outage costs a handful of requests per code instead of one every few seconds.
    /// </summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromDays(1),
    ];

    private ProductPhoto()
    {
        // Driver materialisation.
    }

    private ProductPhoto(Gtin gtin, DateTimeOffset at)
    {
        Gtin = gtin;
        Status = ProductPhotoStatus.Pending;
        RequestedAt = at;
        NextAttemptAt = at;
        UpdatedAt = at;
    }

    public Gtin Gtin { get; private set; }

    public ProductPhotoStatus Status { get; private set; }

    /// <summary>When the worker may look at this code again. Meaningless once ready.</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>Failures in a row; a clean answer, found or not, starts the count over.</summary>
    public int FailedAttempts { get; private set; }

    /// <summary>
    /// Changes whenever the stored picture does, and goes into its address: browsers keep
    /// a photo for a year, and a replaced one must not be served from their cache.
    /// </summary>
    public string? Version { get; private set; }

    /// <summary>Where the photo came from, as its licence asks to be credited: "Open Food Facts".</summary>
    public string? Source { get; private set; }

    /// <summary>The page of the product at the source, for the credit to link to.</summary>
    public string? SourcePage { get; private set; }

    public string? License { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ProductPhoto Request(Gtin gtin, DateTimeOffset at)
    {
        if (gtin.IsInternal)
        {
            throw new ArgumentException(
                $"{gtin.ToDisplay()} is a code the shop minted itself; it has no photo to look up.", nameof(gtin));
        }

        return new ProductPhoto(gtin, at);
    }

    public bool IsDue(DateTimeOffset now) => Status != ProductPhotoStatus.Ready && NextAttemptAt <= now;

    public void Store(string version, string source, string sourcePage, string license, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePage);
        ArgumentException.ThrowIfNullOrWhiteSpace(license);

        Status = ProductPhotoStatus.Ready;
        Version = version;
        Source = source;
        SourcePage = sourcePage;
        License = license;
        FailedAttempts = 0;
        UpdatedAt = at;
    }

    public void MarkMissing(DateTimeOffset at)
    {
        Status = ProductPhotoStatus.Missing;
        FailedAttempts = 0;
        NextAttemptAt = at + RecheckMissingAfter;
        UpdatedAt = at;
    }

    /// <summary>
    /// The attempt could not finish - nothing is known about the code yet, so it stays as it
    /// was and is tried again later.
    /// </summary>
    public void RecordFailure(DateTimeOffset at)
    {
        FailedAttempts++;
        NextAttemptAt = at + RetryDelays[Math.Min(FailedAttempts, RetryDelays.Length) - 1];
        UpdatedAt = at;
    }
}
