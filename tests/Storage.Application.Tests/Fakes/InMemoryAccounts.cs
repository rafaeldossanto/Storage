using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Accounts;
using Storage.Domain.Catalog;

namespace Storage.Application.Tests.Fakes;

/// <summary>A clock the test moves by hand, to cross a lockout or a session expiry.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// Readable stand-in for the real hasher: a hash is "hash:" plus the password, and one that
/// starts with "old:" is right but due for an upgrade. Counts decoy checks so a test can see
/// that an unknown e-mail still spent the time of a real check.
/// </summary>
internal sealed class FakePasswordHasher : IPasswordHasher
{
    public int DecoyChecks { get; private set; }

    public string Hash(string password) => $"hash:{password}";

    public PasswordVerification Verify(string passwordHash, string password) => passwordHash switch
    {
        _ when passwordHash == $"hash:{password}" => PasswordVerification.Succeeded,
        _ when passwordHash == $"old:{password}" => PasswordVerification.SucceededNeedsRehash,
        _ => PasswordVerification.Failed,
    };

    public void VerifyDecoy(string password) => DecoyChecks++;
}

internal sealed class FakeAccessTokenIssuer(TimeProvider clock) : IAccessTokenIssuer
{
    public AccessToken Issue(User user) =>
        new($"access:{user.Id}:{user.TenantId}", clock.GetUtcNow().AddMinutes(15));
}

/// <summary>
/// One in-memory "database" for the account flows, mirroring the unique e-mail index.
/// </summary>
internal sealed class InMemoryAccountStore : IAccountStore
{
    public List<Tenant> Tenants { get; } = [];
    public List<User> Users { get; } = [];
    public List<Session> Sessions { get; } = [];
    public List<Category> Categories { get; } = [];

    public Task<bool> EmailInUseAsync(EmailAddress email, CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.Any(user => user.Email == email));

    public Task<User?> FindUserByEmailAsync(EmailAddress email, CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.FirstOrDefault(user => user.Email == email));

    public Task<User?> FindUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.FirstOrDefault(user => user.Id == userId));

    public Task<Tenant?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tenants.FirstOrDefault(tenant => tenant.Id == tenantId));

    public Task ProvisionAsync(
        Tenant tenant,
        User owner,
        IReadOnlyCollection<Category> categories,
        CancellationToken cancellationToken = default)
    {
        AddUser(owner);
        Tenants.Add(tenant);
        Categories.AddRange(categories);
        return Task.CompletedTask;
    }

    public Task UpdateUserAsync(User user, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<Session?> FindSessionByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(Sessions.FirstOrDefault(session => session.TokenHash == tokenHash));

    public Task AddSessionAsync(Session session, CancellationToken cancellationToken = default)
    {
        Sessions.Add(session);
        return Task.CompletedTask;
    }

    public Task UpdateSessionAsync(Session session, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RevokeAllSessionsAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        foreach (var session in Sessions.Where(session => session.UserId == userId))
        {
            session.Revoke(now);
        }

        return Task.CompletedTask;
    }

    public void AddUser(User user)
    {
        if (Users.Any(existing => existing.Email == user.Email))
        {
            throw UseCaseException.Conflict(ErrorCodes.EmailTaken, "E-mail taken.");
        }

        Users.Add(user);
    }
}

/// <summary>The shop-scoped view over the same in-memory users.</summary>
internal sealed class InMemoryUserRepository(InMemoryAccountStore store, ITenantContext tenant) : IUserRepository
{
    public Task<User?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(OfThisShop().FirstOrDefault(user => user.Id == id));

    public Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<User>>(OfThisShop().ToArray());

    public Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        store.AddUser(user);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(User user, CancellationToken cancellationToken = default) => Task.CompletedTask;

    private IEnumerable<User> OfThisShop() => store.Users.Where(user => user.TenantId == tenant.TenantId);
}
