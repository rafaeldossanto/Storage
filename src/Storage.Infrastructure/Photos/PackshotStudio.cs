using SkiaSharp;
using Storage.Application.Abstractions;

namespace Storage.Infrastructure.Photos;

/// <summary>
/// Makes the picture a product card shows: the package cut out, trimmed to its edges and
/// centred on a white square, as WebP.
/// </summary>
public sealed class PackshotStudio(IBackgroundCutter cutter) : IPackshotStudio
{
    /// <summary>Sharp on a phone screen at card size and in the product dialog, and a few dozen kilobytes.</summary>
    public const int Side = 640;

    /// <summary>The package fills this much of the square; the rest is white margin.</summary>
    private const float Fill = 0.88f;

    /// <summary>Bigger photos are shrunk first: nothing past this helps a 640 px result, and cutting out costs per pixel.</summary>
    private const int MaxWorkingSide = 1600;

    private const int Quality = 85;

    /// <summary>
    /// A mask that keeps almost nothing, or almost everything, is the cutter failing on this
    /// photo - better the whole photo than a sliver of it.
    /// </summary>
    private const double MinProductShare = 0.01;

    private const double MaxProductShare = 0.995;

    /// <summary>Pixels fainter than this do not count when trimming to the package's edges.</summary>
    private const byte TrimAlpha = 24;

    public Task<Packshot> MakePackshotAsync(byte[] photo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        return Task.FromResult(Make(photo));
    }

    internal Packshot Make(byte[] photo)
    {
        using var decoded = Decode(photo);
        using var working = Shrink(decoded);

        var cutout = cutter.Cut(working);

        if (cutout is not null && IsPlausible(cutout.Alpha))
        {
            Apply(working, cutout);
        }

        var bounds = OpaqueBounds(working);

        using var surface = SKSurface.Create(new SKImageInfo(Side, Side, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        using (var image = SKImage.FromBitmap(working))
        {
            canvas.DrawImage(
                image,
                new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                Centred(bounds.Width, bounds.Height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                paint: null);
        }

        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Webp, Quality);

        return new Packshot("image/webp", encoded.ToArray());
    }

    private static SKBitmap Decode(byte[] photo)
    {
        using var data = SKData.CreateCopy(photo);
        using var codec = SKCodec.Create(data)
            ?? throw new UnusablePhotoException("The photo is not an image format that can be read.");

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        return SKBitmap.Decode(codec, info)
            ?? throw new UnusablePhotoException("The photo could not be decoded.");
    }

    private static SKBitmap Shrink(SKBitmap photo)
    {
        var longest = Math.Max(photo.Width, photo.Height);
        var scale = Math.Min(1.0, (double)MaxWorkingSide / longest);

        return photo.Resize(
            new SKImageInfo(
                Math.Max(1, (int)Math.Round(photo.Width * scale)),
                Math.Max(1, (int)Math.Round(photo.Height * scale)),
                SKColorType.Rgba8888,
                SKAlphaType.Unpremul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    private static bool IsPlausible(byte[] alpha)
    {
        var product = alpha.Count(value => value >= 128);
        var share = (double)product / alpha.Length;

        return share is >= MinProductShare and <= MaxProductShare;
    }

    /// <summary>
    /// Writes the mask into the photo's alpha. Where the backdrop is known, an edge pixel's
    /// colour is un-blended from it: seen through its partial alpha on white, a pixel that
    /// still held some black would draw a dark rim.
    /// </summary>
    private static void Apply(SKBitmap photo, Cutout cutout)
    {
        var pixels = photo.GetPixelSpan();

        for (var index = 0; index < cutout.Alpha.Length; index++)
        {
            var alpha = cutout.Alpha[index];
            var offset = index * 4;

            if (cutout.Backdrop is { } backdrop && alpha is > 0 and < 255)
            {
                var share = alpha / 255f;
                pixels[offset] = Unblend(pixels[offset], backdrop.Red, share);
                pixels[offset + 1] = Unblend(pixels[offset + 1], backdrop.Green, share);
                pixels[offset + 2] = Unblend(pixels[offset + 2], backdrop.Blue, share);
            }

            pixels[offset + 3] = Math.Min(pixels[offset + 3], alpha);
        }
    }

    /// <summary>Solves seen = share · product + (1 − share) · backdrop for the product.</summary>
    private static byte Unblend(byte seen, byte backdrop, float share) =>
        (byte)Math.Clamp(MathF.Round((seen - (1 - share) * backdrop) / share), 0, 255);

    private static SKRectI OpaqueBounds(SKBitmap photo)
    {
        var pixels = photo.GetPixelSpan();
        int left = photo.Width, top = photo.Height, right = -1, bottom = -1;

        for (var y = 0; y < photo.Height; y++)
        {
            for (var x = 0; x < photo.Width; x++)
            {
                if (pixels[(y * photo.Width + x) * 4 + 3] <= TrimAlpha)
                {
                    continue;
                }

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        // Nothing opaque at all cannot come out of a plausible mask; the whole photo, then.
        return right < 0
            ? new SKRectI(0, 0, photo.Width, photo.Height)
            : new SKRectI(left, top, right + 1, bottom + 1);
    }

    /// <summary>The largest rectangle of the package's proportions that fits the margin, centred.</summary>
    private static SKRect Centred(int width, int height)
    {
        var room = Side * Fill;
        var scale = room / Math.Max(width, height);
        var drawnWidth = width * scale;
        var drawnHeight = height * scale;
        var left = (Side - drawnWidth) / 2;
        var top = (Side - drawnHeight) / 2;

        return new SKRect(left, top, left + drawnWidth, top + drawnHeight);
    }
}
