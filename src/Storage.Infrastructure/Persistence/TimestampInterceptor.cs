using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Storage.Domain.Common;

namespace Storage.Infrastructure.Persistence;

/// <summary>
/// Stamps CreatedAt and UpdatedAt on every save.
/// </summary>
/// <remarks>
/// Written here rather than inside the entities so the domain never reads the clock: the
/// values come from an injected <see cref="TimeProvider"/>, which a test can freeze.
/// The properties have private setters, so they are written through the change tracker.
/// </remarks>
public sealed class TimestampInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();

        foreach (var entry in context.ChangeTracker.Entries<ITimestamped>())
        {
            if (entry.State is EntityState.Added)
            {
                entry.Property(nameof(ITimestamped.CreatedAt)).CurrentValue = now;
                entry.Property(nameof(ITimestamped.UpdatedAt)).CurrentValue = now;
            }
            else if (entry.State is EntityState.Modified)
            {
                entry.Property(nameof(ITimestamped.UpdatedAt)).CurrentValue = now;
            }
        }
    }
}
