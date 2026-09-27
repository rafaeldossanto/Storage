using Storage.Application.Abstractions;
using Storage.Application.Catalog;
using Storage.Infrastructure.Photos;

namespace Storage.Api.Jobs;

/// <summary>
/// Gives products their photos: works through the codes waiting for one, one at a time,
/// and checks for new ones every few seconds when none is waiting.
/// </summary>
/// <remarks>
/// On start it also asks for the photo of every code already registered without one, so
/// products from before photos existed get theirs too. Running on two servers at once is
/// safe: claiming a code pushes its next attempt ahead, so only one server works on it.
/// </remarks>
public sealed class ProductPhotoJob(
    IServiceScopeFactory scopes,
    IBackgroundCutter cutter,
    ProductPhotoOptions options,
    ILogger<ProductPhotoJob> logger,
    TimeProvider clock) : BackgroundService
{
    /// <summary>How soon a product registered while the queue was empty gets looked at.</summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);

    /// <summary>After an error outside any one code - the database away - wait longer before trying again.</summary>
    public static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (cutter is IsnetBackgroundCutter)
        {
            logger.LogInformation("Product photos are cut out with the model at {Path}", options.CutoutModelPath);
        }
        else
        {
            logger.LogWarning(
                "No cutout model at {Path}: only photos shot on a plain backdrop are cut out, the rest are placed on white whole",
                options.CutoutModelPath);
        }

        await RequestCatalogedAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = await ProcessNextAsync(stoppingToken);

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, clock, stoppingToken);
            }
        }
    }

    private async Task RequestCatalogedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IProductPhotoStore>().RequestAllCatalogedAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the backlog waits for the next start; new products still get photos.
            logger.LogError(exception, "Could not ask for the photos of products registered without one");
        }
    }

    /// <summary>Works on one code. Returns how long to wait before the next: none while there is work.</summary>
    private async Task<TimeSpan> ProcessNextAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var attempt = await scope.ServiceProvider.GetRequiredService<ProductPhotoService>().ProcessNextAsync(cancellationToken);

            if (attempt is null)
            {
                return IdleDelay;
            }

            switch (attempt.Outcome)
            {
                case PhotoAttemptOutcome.Stored:
                    logger.LogInformation("Stored the photo of {Gtin} from {Source}", attempt.Gtin, attempt.Source);
                    break;
                case PhotoAttemptOutcome.Missing:
                    logger.LogInformation(attempt.Error, "No photo for {Gtin}; looking again in 30 days", attempt.Gtin);
                    break;
                default:
                    logger.LogWarning(attempt.Error, "Could not get the photo of {Gtin}; retrying later", attempt.Gtin);
                    break;
            }

            return TimeSpan.Zero;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The photo worker failed; trying again in {Delay}", ErrorDelay);
            return ErrorDelay;
        }
    }
}
