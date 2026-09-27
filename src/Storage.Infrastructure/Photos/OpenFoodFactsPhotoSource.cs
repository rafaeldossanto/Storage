using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Storage.Application.Abstractions;
using Storage.Domain.ValueObjects;

namespace Storage.Infrastructure.Photos;

/// <summary>
/// The front-of-package photo from Open Food Facts and its sister databases, looked up by
/// barcode.
/// </summary>
/// <remarks>
/// The photos are contributed by the public under CC BY-SA 3.0: free to use, including
/// commercially, as long as the source is credited - which is why every stored photo keeps
/// where it came from, and the screen shows it.
/// </remarks>
internal sealed partial class OpenFoodFactsPhotoSource(HttpClient http, ProductPhotoOptions options) : IProductPhotoSource
{
    private const string License = "CC BY-SA 3.0";

    /// <summary>More than any real product photo; a guard against a runaway download.</summary>
    private const long MaxPhotoBytes = 15 * 1024 * 1024;

    public async Task<SourcePhoto?> FindAsync(Gtin gtin, CancellationToken cancellationToken = default)
    {
        // The databases key products by the printed code: 13 digits for an EAN-13, 8 for an
        // EAN-8 - the display form, not the 14 digit one.
        var code = gtin.ToDisplay();

        foreach (var site in options.Sites)
        {
            var front = await FrontPhotoAddressAsync(site, code, cancellationToken);

            if (front is null)
            {
                continue;
            }

            // The full resolution cuts out better; the 400 px version is the fallback.
            var content = await DownloadAsync(FullSize(front), cancellationToken)
                ?? await DownloadAsync(front, cancellationToken);

            if (content is not null)
            {
                return new SourcePhoto(content, site.Name, new Uri(site.BaseAddress, $"product/{code}").ToString(), License);
            }
        }

        return null;
    }

    private async Task<Uri?> FrontPhotoAddressAsync(PhotoSite site, string code, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            new Uri(site.BaseAddress, $"api/v2/product/{code}?fields=code,image_front_url"),
            cancellationToken);

        // An unknown code is an answer, not a failure: the next site may know it.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var root = json.RootElement;

        if (!Found(root)
            || !root.TryGetProperty("product", out var product)
            || !product.TryGetProperty("image_front_url", out var address)
            || address.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(address.GetString(), UriKind.Absolute, out var photo))
        {
            return null;
        }

        // Only a photo on the site's own image servers is fetched: the address comes out of
        // a database anyone can edit, and must never send this server anywhere else.
        return IsOnSite(photo, site) ? photo : null;
    }

    private async Task<byte[]?> DownloadAsync(Uri address, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaxPhotoBytes)
        {
            return null;
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;

        // Counted as it arrives: a server may send no length, or the wrong one.
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxPhotoBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>API v2 answers "status": 1 for a known product; v3 says "success".</summary>
    private static bool Found(JsonElement root) =>
        root.TryGetProperty("status", out var status)
        && (status.ValueKind == JsonValueKind.Number ? status.GetInt32() == 1 : status.GetString() == "success");

    /// <summary>"…/front_pt.54.400.jpg" becomes "…/front_pt.54.full.jpg".</summary>
    private static Uri FullSize(Uri photo) =>
        new(SizedPhoto().Replace(photo.AbsoluteUri, ".full.jpg"));

    /// <summary>
    /// images.openfoodfacts.org for world.openfoodfacts.org: same registrable domain, over
    /// HTTPS.
    /// </summary>
    private static bool IsOnSite(Uri photo, PhotoSite site)
    {
        var siteHost = site.BaseAddress.Host;
        var domain = siteHost[(siteHost.IndexOf('.', StringComparison.Ordinal) + 1)..];

        return photo.Scheme == Uri.UriSchemeHttps
            && (photo.Host == domain || photo.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\.\d+\.jpg$", RegexOptions.IgnoreCase)]
    private static partial Regex SizedPhoto();
}
