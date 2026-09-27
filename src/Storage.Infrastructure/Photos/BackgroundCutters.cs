using Microsoft.ML.OnnxRuntime;
using SkiaSharp;

namespace Storage.Infrastructure.Photos;

/// <summary>Decides which pixels of a photo are the product.</summary>
public interface IBackgroundCutter
{
    /// <param name="photo">RGBA, 8 bits per channel, not premultiplied.</param>
    /// <returns>Null when it cannot tell: the photo is then kept whole.</returns>
    Cutout? Cut(SKBitmap photo);
}

/// <param name="Alpha">One value per pixel, row by row: 0 is background, 255 is product.</param>
/// <param name="Backdrop">
/// The colour of the background, when it was one plain colour. The edge pixels blend it
/// with the product; knowing it lets them be un-blended, so no dark rim stays around a
/// package cut from black.
/// </param>
public sealed record Cutout(byte[] Alpha, SKColor? Backdrop);

/// <summary>
/// Cuts a product out of a plain backdrop - the black, grey or white studio background most
/// catalogue photos have - by flooding the backdrop colour in from the edges.
/// </summary>
/// <remarks>
/// No model, nothing to download. It gives up, keeping the photo whole, when the edges are
/// not one colour: a photo taken on a shelf or a lap is left as it is.
/// </remarks>
public sealed class SolidBackdropCutter : IBackgroundCutter
{
    /// <summary>How far, in RGB distance, a pixel may drift from the backdrop and still be it: JPEG noise, a soft shadow.</summary>
    internal const int Tolerance = 40;

    /// <summary>Share of the edge that must be the backdrop colour for the photo to count as shot on one.</summary>
    internal const double PlainEdgeShare = 0.9;

    public Cutout? Cut(SKBitmap photo)
    {
        ArgumentNullException.ThrowIfNull(photo);

        var width = photo.Width;
        var height = photo.Height;
        var pixels = photo.GetPixelSpan();
        var edge = EdgeIndexes(width, height);

        var backdrop = MedianColor(pixels, edge);
        var plainEdge = 0;

        foreach (var index in edge)
        {
            if (Distance(pixels, index, backdrop) <= Tolerance)
            {
                plainEdge++;
            }
        }

        if (plainEdge < edge.Length * PlainEdgeShare)
        {
            return null;
        }

        var background = Flood(pixels, width, height, edge, backdrop);
        var alpha = new byte[width * height];

        for (var index = 0; index < alpha.Length; index++)
        {
            if (background[index])
            {
                continue;
            }

            // Next to the backdrop, a pixel is part product, part backdrop: fade it by how
            // close it is, instead of a hard staircase edge.
            var distance = Distance(pixels, index, backdrop);
            alpha[index] = TouchesBackground(background, index, width, height) && distance < Tolerance * 2
                ? (byte)Math.Clamp((distance - Tolerance) * 255 / Tolerance, 0, 255)
                : (byte)255;
        }

        return new Cutout(alpha, backdrop);
    }

    private static int[] EdgeIndexes(int width, int height)
    {
        var edge = new List<int>(2 * (width + height));

        for (var x = 0; x < width; x++)
        {
            edge.Add(x);
            edge.Add((height - 1) * width + x);
        }

        for (var y = 1; y < height - 1; y++)
        {
            edge.Add(y * width);
            edge.Add(y * width + width - 1);
        }

        return edge.ToArray();
    }

    /// <summary>The median of each channel: one stray bright pixel on the edge does not move it.</summary>
    private static SKColor MedianColor(ReadOnlySpan<byte> pixels, int[] indexes)
    {
        var channels = new byte[3][];

        for (var channel = 0; channel < 3; channel++)
        {
            var values = new byte[indexes.Length];

            for (var i = 0; i < indexes.Length; i++)
            {
                values[i] = pixels[indexes[i] * 4 + channel];
            }

            Array.Sort(values);
            channels[channel] = values;
        }

        var middle = indexes.Length / 2;
        return new SKColor(channels[0][middle], channels[1][middle], channels[2][middle]);
    }

    /// <summary>
    /// Every pixel reachable from the edge through backdrop-coloured pixels. Compared with
    /// the backdrop, not with the neighbour, so a gradient cannot carry the flood into the
    /// product step by step.
    /// </summary>
    private static bool[] Flood(ReadOnlySpan<byte> pixels, int width, int height, int[] edge, SKColor backdrop)
    {
        var background = new bool[width * height];
        var pending = new Stack<int>();

        foreach (var index in edge)
        {
            if (Distance(pixels, index, backdrop) <= Tolerance)
            {
                background[index] = true;
                pending.Push(index);
            }
        }

        while (pending.TryPop(out var index))
        {
            var x = index % width;
            var y = index / width;

            Visit(x - 1, y, pixels, background, pending, width, height, backdrop);
            Visit(x + 1, y, pixels, background, pending, width, height, backdrop);
            Visit(x, y - 1, pixels, background, pending, width, height, backdrop);
            Visit(x, y + 1, pixels, background, pending, width, height, backdrop);
        }

        return background;
    }

    private static void Visit(
        int x, int y, ReadOnlySpan<byte> pixels, bool[] background, Stack<int> pending, int width, int height, SKColor backdrop)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        var index = y * width + x;

        if (!background[index] && Distance(pixels, index, backdrop) <= Tolerance)
        {
            background[index] = true;
            pending.Push(index);
        }
    }

    private static bool TouchesBackground(bool[] background, int index, int width, int height)
    {
        var x = index % width;
        var y = index / width;

        return (x > 0 && background[index - 1])
            || (x < width - 1 && background[index + 1])
            || (y > 0 && background[index - width])
            || (y < height - 1 && background[index + width]);
    }

    private static int Distance(ReadOnlySpan<byte> pixels, int index, SKColor color)
    {
        var red = pixels[index * 4] - color.Red;
        var green = pixels[index * 4 + 1] - color.Green;
        var blue = pixels[index * 4 + 2] - color.Blue;
        return (int)Math.Sqrt(red * red + green * green + blue * blue);
    }
}

/// <summary>
/// Cuts a product out of any background with ISNet ("DIS", general use), the model rembg
/// ships: whatever the photo was taken on - a shelf, a counter, a hand.
/// </summary>
/// <remarks>
/// Pre- and post-processing follow rembg's, which the model was published with: the photo
/// stretched to 1024 x 1024, scaled by its brightest value and centred on 0.5; the
/// prediction min-max normalised into an alpha mask and stretched back.
/// </remarks>
public sealed class IsnetBackgroundCutter : IBackgroundCutter, IDisposable
{
    internal const int Side = 1024;

    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly string _output;

    public IsnetBackgroundCutter(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        _session = new InferenceSession(modelPath);
        _input = _session.InputNames[0];
        _output = _session.OutputNames[0];
    }

    public Cutout? Cut(SKBitmap photo)
    {
        ArgumentNullException.ThrowIfNull(photo);

        using var square = photo.Resize(
            new SKImageInfo(Side, Side, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            new SKSamplingOptions(SKCubicResampler.CatmullRom));

        var tensor = ToTensor(square.GetPixelSpan(), Side);

        using var input = OrtValue.CreateTensorValueFromMemory(tensor, [1, 3, Side, Side]);
        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, [_input], [input], [_output]);

        var mask = ToAlpha(outputs[0].GetTensorDataAsSpan<float>());

        return new Cutout(Stretch(mask, photo.Width, photo.Height), Backdrop: null);
    }

    public void Dispose() => _session.Dispose();

    /// <summary>RGBA pixels to the model's input: planar RGB (NCHW), divided by the brightest value, minus 0.5.</summary>
    internal static float[] ToTensor(ReadOnlySpan<byte> rgba, int side)
    {
        var plane = side * side;
        byte brightest = 0;

        for (var index = 0; index < plane; index++)
        {
            brightest = Math.Max(brightest, Math.Max(rgba[index * 4], Math.Max(rgba[index * 4 + 1], rgba[index * 4 + 2])));
        }

        var scale = 1f / Math.Max((float)brightest, 1e-6f);
        var tensor = new float[3 * plane];

        for (var index = 0; index < plane; index++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                tensor[channel * plane + index] = rgba[index * 4 + channel] * scale - 0.5f;
            }
        }

        return tensor;
    }

    /// <summary>The prediction, stretched to span 0 to 255.</summary>
    internal static byte[] ToAlpha(ReadOnlySpan<float> prediction)
    {
        var lowest = float.MaxValue;
        var highest = float.MinValue;

        foreach (var value in prediction)
        {
            lowest = Math.Min(lowest, value);
            highest = Math.Max(highest, value);
        }

        var range = Math.Max(highest - lowest, 1e-6f);
        var alpha = new byte[prediction.Length];

        for (var index = 0; index < alpha.Length; index++)
        {
            alpha[index] = (byte)Math.Round((prediction[index] - lowest) / range * 255);
        }

        return alpha;
    }

    private static byte[] Stretch(byte[] mask, int width, int height)
    {
        using var square = new SKBitmap(new SKImageInfo(Side, Side, SKColorType.Gray8, SKAlphaType.Opaque));
        mask.CopyTo(square.GetPixelSpan());

        using var stretched = square.Resize(
            new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque),
            new SKSamplingOptions(SKFilterMode.Linear));

        return stretched.GetPixelSpan().ToArray();
    }
}
