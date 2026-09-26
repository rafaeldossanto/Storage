namespace Storage.Domain.Common;

/// <summary>
/// Rows that record when they were created and last changed.
/// </summary>
/// <remarks>
/// The values are filled in at save time from a single clock, so no entity has to reach
/// for <c>DateTimeOffset.UtcNow</c> on its own - which also keeps the domain testable
/// without freezing time.
/// </remarks>
public interface ITimestamped
{
    DateTimeOffset CreatedAt { get; }

    DateTimeOffset UpdatedAt { get; }
}
