using Storage.Application.Abstractions;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Stock;

public sealed record ExpirySweepDto(int Batches, int Units, long ValueCents);

/// <summary>
/// Takes expired batches off sale and records what they held as a loss.
/// </summary>
/// <remarks>
/// Nothing is deleted: an expired batch stays, marked expired, and its units become an
/// ExpiryLoss movement valued at what they cost - the line the loss report adds up. Running
/// it twice changes nothing the second time, because an expired batch is no longer
/// available and so is no longer found.
/// </remarks>
public sealed class ExpiryService(IStockStore stock, IShopCalendar calendar, TimeProvider clock)
{
    public async Task<ExpirySweepDto> ExpireDueAsync(CancellationToken cancellationToken = default)
    {
        // "Expired" is decided on the shop's calendar: the 18th is still sellable all day on
        // the 18th where the shop is, whatever the date already is in UTC.
        var today = await calendar.TodayAsync(cancellationToken);
        var due = await stock.ListExpiredBatchesAsync(today, cancellationToken);

        if (due.Count == 0)
        {
            return new ExpirySweepDto(0, 0, 0);
        }

        var now = clock.GetUtcNow();
        var changes = new StockChanges();
        var units = 0;
        var value = Money.Zero;

        foreach (var batch in due)
        {
            var lost = changes.Change(batch, expiring => expiring.Expire());

            if (lost > 0)
            {
                changes.Record(StockMovement.ExpiryLoss(batch, lost, now));
                units += lost;
                value += batch.UnitCost * lost;
            }
        }

        await stock.CommitAsync(changes, cancellationToken);

        return new ExpirySweepDto(due.Count, units, value.Cents);
    }
}
