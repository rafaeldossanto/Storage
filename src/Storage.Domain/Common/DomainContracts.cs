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

    void MarkCreated(DateTimeOffset at);

    void MarkUpdated(DateTimeOffset at);
}

/// <summary>
/// Data that belongs to one shop.
/// </summary>
/// <remarks>
/// The platform is shared, the data is not: every record carries the shop it belongs to,
/// and every read is filtered by it. This sits in the domain rather than in persistence
/// because "this category belongs to that shop" is a fact about the business, not a
/// detail of how rows are stored.
/// </remarks>
public interface ITenantScoped
{
    Guid TenantId { get; }
}
