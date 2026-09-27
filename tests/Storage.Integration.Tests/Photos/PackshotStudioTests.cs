using SkiaSharp;
using Storage.Application.Abstractions;
using Storage.Infrastructure.Photos;

namespace Storage.Integration.Tests.Photos;

/// <summary>
/// The packshot made from photos drawn in the test: a red can on black, as a studio shoots
/// it, and the same can on a busy background, as a phone does.
/// </summary>
public sealed class PackshotStudioTests
{
    private static readonly SKColor CanRed = new(200, 20, 30);

    private readonly PackshotStudio _studio = new(new SolidBackdropCutter());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_can_shot_on_black_comes_out_alone_on_white_filling_the_frame()
    {
        var photo = Draw(400, 300, background: (_, _) => SKColors.Black);

        var shot = await _studio.MakePackshotAsync(photo, Token);

        Assert.Equal("image/webp", shot.ContentType);
        using var result = SKBitmap.Decode(shot.Content);
        Assert.Equal(PackshotStudio.Side, result.Width);
        Assert.Equal(PackshotStudio.Side, result.Height);

        // The black is gone - white where it was, around the can and in the corners.
        AssertWhite(result.GetPixel(5, 5));
        AssertWhite(result.GetPixel(100, 320));

        // The can, trimmed to its edges and scaled up: twice as tall as wide, it spans 88%
        // of the height, centred.
        AssertRed(result.GetPixel(320, 320));
        AssertRed(result.GetPixel(320, 50));
        AssertWhite(result.GetPixel(320, 30));
    }

    [Fact]
    public async Task A_photo_without_a_plain_backdrop_is_kept_whole_on_white()
    {
        var noise = new Random(7);
        var photo = Draw(400, 300, background: (_, _) =>
            new SKColor((byte)noise.Next(256), (byte)noise.Next(256), (byte)noise.Next(256)));

        var shot = await _studio.MakePackshotAsync(photo, Token);

        using var result = SKBitmap.Decode(shot.Content);

        // Wider than tall, the whole photo spans 88% of the width: white bands above and
        // below, the busy background inside them.
        AssertWhite(result.GetPixel(320, 60));
        Assert.False(IsWhite(result.GetPixel(60, 320)) && IsWhite(result.GetPixel(70, 330)));
    }

    [Fact]
    public async Task Bytes_that_are_not_a_picture_are_unusable()
    {
        await Assert.ThrowsAsync<UnusablePhotoException>(
            () => _studio.MakePackshotAsync("não é uma foto"u8.ToArray(), Token));
    }

    [Fact]
    public void The_model_input_is_planar_rgb_scaled_by_the_brightest_value_around_zero()
    {
        // A 2 x 2 photo, RGBA. The brightest value anywhere is 200; alpha is ignored.
        byte[] rgba =
        [
            200, 100, 0, 255, /**/ 50, 0, 100, 255,
            0, 0, 0, 255, /**/ 100, 100, 100, 255,
        ];

        var tensor = IsnetBackgroundCutter.ToTensor(rgba, side: 2);

        // Channel planes one after the other - all reds, then all greens, then all blues -
        // each value divided by 200, minus 0.5.
        Assert.Equal(
            [
                0.5f, -0.25f, -0.5f, 0f,
                0f, -0.5f, -0.5f, 0f,
                -0.5f, 0f, -0.5f, 0f,
            ],
            tensor,
            new Tolerance());
    }

    [Fact]
    public void The_prediction_is_stretched_to_span_the_whole_alpha_range()
    {
        var alpha = IsnetBackgroundCutter.ToAlpha([0.2f, 0.6f, 1.0f]);

        Assert.Equal([0, 128, 255], alpha);
    }

    /// <summary>A photo: the background, and a red can twice as tall as wide in the middle, as PNG.</summary>
    private static byte[] Draw(int width, int height, Func<int, int, SKColor> background)
    {
        using var bitmap = new SKBitmap(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var insideCan = Math.Abs(x - width / 2) < 50 && Math.Abs(y - height / 2) < 100;
                bitmap.SetPixel(x, y, insideCan ? CanRed : background(x, y));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static bool IsWhite(SKColor color) => color is { Red: > 240, Green: > 240, Blue: > 240 };

    private static void AssertWhite(SKColor color) => Assert.True(IsWhite(color), $"Expected white, got {color}.");

    private static void AssertRed(SKColor color) =>
        Assert.True(color is { Red: > 150, Green: < 70, Blue: < 80 }, $"Expected the can's red, got {color}.");

    private sealed class Tolerance : IEqualityComparer<float>
    {
        public bool Equals(float x, float y) => Math.Abs(x - y) < 1e-5f;

        public int GetHashCode(float value) => 0;
    }
}
