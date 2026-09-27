namespace Storage.Application.Abstractions;

/// <summary>
/// Turns a photo of a package into a catalogue shot: the package alone, cut out from
/// whatever it was photographed on, centred on white.
/// </summary>
public interface IPackshotStudio
{
    /// <exception cref="UnusablePhotoException">The bytes are not a picture it can read.</exception>
    Task<Packshot> MakePackshotAsync(byte[] photo, CancellationToken cancellationToken = default);
}

public sealed record Packshot(string ContentType, byte[] Content);

/// <summary>
/// A photo that will never become a packshot, however often it is tried: not an image, or
/// one too broken to decode. The code is treated as having no photo.
/// </summary>
public sealed class UnusablePhotoException(string message, Exception? innerException = null)
    : Exception(message, innerException);
