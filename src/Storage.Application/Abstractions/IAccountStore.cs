using Storage.Domain.Accounts;
using Storage.Domain.Catalog;

namespace Storage.Application.Abstractions;

/// <summary>
/// The only data access that is NOT scoped to the current shop.
/// </summary>
/// <remarks>
/// Signing up, signing in and refreshing happen before anyone knows which shop the caller
/// belongs to - that is what they exist to find out. Everything those flows need lives
/// here, in one place, so the unscoped surface is small and easy to audit. Anything that
/// runs after sign-in goes through the shop-scoped repositories instead.
/// </remarks>
public interface IAccountStore
{
    Task<bool> EmailInUseAsync(EmailAddress email, CancellationToken cancellationToken = default);

    Task<User?> FindUserByEmailAsync(EmailAddress email, CancellationToken cancellationToken = default);

    Task<User?> FindUserAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<Tenant?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a shop with its owner and starting catalogue. The owner goes in first: the
    /// unique e-mail index is what settles two sign-ups racing for the same address, and
    /// losing that race must leave nothing behind.
    /// </summary>
    Task ProvisionAsync(
        Tenant tenant,
        User owner,
        IReadOnlyCollection<Category> categories,
        CancellationToken cancellationToken = default);

    Task UpdateUserAsync(User user, CancellationToken cancellationToken = default);

    Task<Session?> FindSessionByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    Task AddSessionAsync(Session session, CancellationToken cancellationToken = default);

    Task UpdateSessionAsync(Session session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires a session and starts the one that replaces it - only if nobody retired it
    /// first. False when another refresh with the same token got there a moment earlier,
    /// which is the token being used twice.
    /// </summary>
    Task<bool> RotateSessionAsync(Session retired, Session next, CancellationToken cancellationToken = default);

    Task RevokeAllSessionsAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken = default);
}
