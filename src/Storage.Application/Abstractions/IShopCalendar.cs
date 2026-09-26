namespace Storage.Application.Abstractions;

/// <summary>
/// Today, on the current shop's own calendar.
/// </summary>
/// <remarks>
/// The server runs in UTC, but whether a batch has expired, or a date typed at receiving is
/// already in the past, depends on the date where the shop is. Late on the 17th in São Paulo
/// it is already the 18th in UTC.
/// </remarks>
public interface IShopCalendar
{
    Task<DateOnly> TodayAsync(CancellationToken cancellationToken = default);

    /// <summary>Midnight of <paramref name="date"/> where the shop is, as an instant.</summary>
    Task<DateTimeOffset> StartOfDayAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>The shop's IANA time zone, e.g. "America/Sao_Paulo".</summary>
    Task<string> TimeZoneIdAsync(CancellationToken cancellationToken = default);
}
