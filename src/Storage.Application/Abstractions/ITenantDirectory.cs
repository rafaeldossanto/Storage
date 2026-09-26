namespace Storage.Application.Abstractions;

/// <summary>
/// The shops on the platform, for background work that visits each one in turn. Nothing a
/// request can reach: a request only ever sees its own shop.
/// </summary>
public interface ITenantDirectory
{
    Task<IReadOnlyList<Guid>> ListActiveTenantIdsAsync(CancellationToken cancellationToken = default);
}
