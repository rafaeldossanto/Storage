using Storage.Api.Tenancy;
using Storage.Application.Abstractions;
using Storage.Application.Stock;

namespace Storage.Api.Jobs;

/// <summary>
/// Visits every active shop and takes its expired batches off sale - at start-up, then every
/// few hours.
/// </summary>
/// <remarks>
/// Each shop runs in its own service scope acting as that shop, so everything it touches is
/// filtered exactly as a request from that shop would be. One shop failing is logged and
/// skipped, never allowed to stop the others; it is retried on the next run. Running on two
/// servers at once is safe: the compare-and-set write lets one of them win each batch and
/// the other gets a conflict it logs.
/// </remarks>
public sealed class ExpirySweepJob(
    IServiceScopeFactory scopes,
    ILogger<ExpirySweepJob> logger,
    TimeProvider clock) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Straight away, not only on the first tick: a server that restarts often would
        // otherwise leave expired goods on sale for hours.
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval, clock);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    public async Task<IReadOnlyDictionary<Guid, ExpirySweepDto>> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> shops;

        await using (var scope = scopes.CreateAsyncScope())
        {
            shops = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListActiveTenantIdsAsync(cancellationToken);
        }

        var results = new Dictionary<Guid, ExpirySweepDto>();

        foreach (var shop in shops)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantOverride>().ActAs(shop);

                var sweep = await scope.ServiceProvider.GetRequiredService<ExpiryService>().ExpireDueAsync(cancellationToken);
                results[shop] = sweep;

                if (sweep.Batches > 0)
                {
                    logger.LogInformation(
                        "Expired {Batches} batches ({Units} units, {ValueCents} cents at cost) for tenant {TenantId}",
                        sweep.Batches, sweep.Units, sweep.ValueCents, shop);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Expiry sweep failed for tenant {TenantId}; retrying next run", shop);
            }
        }

        // One line per run even when nothing expired: silence would look the same as a job
        // that never started.
        logger.LogInformation(
            "Expiry sweep visited {Shops} shops, {Failed} failed, {Batches} batches expired",
            shops.Count,
            shops.Count - results.Count,
            results.Values.Sum(sweep => sweep.Batches));

        return results;
    }
}
