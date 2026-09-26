using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Accounts;
using Storage.Domain.Catalog;

namespace Storage.Infrastructure.Persistence;

/// <summary>
/// Unscoped account data for the sign-up, sign-in and refresh flows. See
/// <see cref="IAccountStore"/> for why this is the one place allowed to read across shops.
/// </summary>
public sealed class MongoAccountStore(MongoStorageContext context, TimeProvider clock) : IAccountStore
{
    private const string EmailTaken = "This e-mail already has an account.";

    public async Task<bool> EmailInUseAsync(EmailAddress email, CancellationToken cancellationToken = default) =>
        await context.Users.Find(ByEmail(email)).AnyAsync(cancellationToken);

    public async Task<User?> FindUserByEmailAsync(EmailAddress email, CancellationToken cancellationToken = default) =>
        await context.Users.Find(ByEmail(email)).FirstOrDefaultAsync(cancellationToken);

    public async Task<User?> FindUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await context.Users
            .Find(Builders<User>.Filter.Eq(user => user.Id, userId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<Tenant?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await context.Tenants
            .Find(Builders<Tenant>.Filter.Eq(tenant => tenant.Id, tenantId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task ProvisionAsync(
        Tenant tenant,
        User owner,
        IReadOnlyCollection<Category> categories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(categories);

        var now = clock.GetUtcNow();
        tenant.MarkCreated(now);
        owner.MarkCreated(now);

        // One transaction: a shop exists with its owner and its starting categories, or not at
        // all - a crash halfway can no longer leave a shop behind without categories. The
        // owner still goes first, so the unique e-mail index settles a race between two
        // sign-ups before anything else is attempted.
        await DuplicateKey.GuardAsync(
            () => context.InTransactionAsync(
                async (session, token) =>
                {
                    await context.Users.InsertOneAsync(session, owner, cancellationToken: token);
                    await context.Tenants.InsertOneAsync(session, tenant, cancellationToken: token);

                    if (categories.Count > 0)
                    {
                        await context.Categories.InsertManyAsync(session, categories, cancellationToken: token);
                    }
                },
                cancellationToken),
            ErrorCodes.EmailTaken,
            EmailTaken);
    }

    public async Task UpdateUserAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        user.MarkUpdated(clock.GetUtcNow());

        await context.Users.ReplaceOneAsync(
            Builders<User>.Filter.Eq(stored => stored.Id, user.Id),
            user,
            new ReplaceOptions(),
            cancellationToken);
    }

    public async Task<Session?> FindSessionByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default) =>
        await context.Sessions
            .Find(Builders<Session>.Filter.Eq(session => session.TokenHash, tokenHash))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<Session?> FindSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
        await context.Sessions
            .Find(Builders<Session>.Filter.Eq(session => session.Id, id))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task AddSessionAsync(Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await context.Sessions.InsertOneAsync(session, options: null, cancellationToken);
    }

    public async Task UpdateSessionAsync(Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await context.Sessions.ReplaceOneAsync(
            Builders<Session>.Filter.Eq(stored => stored.Id, session.Id),
            session,
            new ReplaceOptions(),
            cancellationToken);
    }

    public async Task<bool> RotateSessionAsync(Session retired, Session next, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retired);
        ArgumentNullException.ThrowIfNull(next);

        // Compare-and-set: two refreshes carrying the same token both read it as active, and
        // without this both would hand out a new session - the family forks, and a stolen
        // token used in the same instant as the real one would survive. Only one retires it.
        var stillActive = Builders<Session>.Filter.Eq(stored => stored.Id, retired.Id)
            & Builders<Session>.Filter.Eq(stored => stored.RevokedAt, null);

        var retire = Builders<Session>.Update
            .Set(stored => stored.RevokedAt, retired.RevokedAt)
            .Set(stored => stored.ReplacedBy, retired.ReplacedBy);

        var result = await context.Sessions.UpdateOneAsync(stillActive, retire, cancellationToken: cancellationToken);

        if (result.ModifiedCount == 0)
        {
            return false;
        }

        // Retired first, then started: a crash in between costs a sign-in, never a second
        // live session.
        await context.Sessions.InsertOneAsync(next, options: null, cancellationToken);
        return true;
    }

    public async Task<bool> SaveSalesPinAsync(Tenant tenant, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        // A shop from before the PIN existed has no version stored at all; that reads as 0.
        var versionAsRead = expectedVersion == 0
            ? Builders<Tenant>.Filter.Or(
                Builders<Tenant>.Filter.Eq(stored => stored.SalesPinVersion, 0L),
                Builders<Tenant>.Filter.Exists(stored => stored.SalesPinVersion, false))
            : Builders<Tenant>.Filter.Eq(stored => stored.SalesPinVersion, expectedVersion);

        var result = await context.Tenants.UpdateOneAsync(
            Builders<Tenant>.Filter.Eq(stored => stored.Id, tenant.Id) & versionAsRead,
            Builders<Tenant>.Update
                .Set(stored => stored.SalesPinHash, tenant.SalesPinHash)
                .Set(stored => stored.SalesPinFailures, tenant.SalesPinFailures)
                .Set(stored => stored.SalesPinLockedUntil, tenant.SalesPinLockedUntil)
                .Set(stored => stored.SalesPinVersion, tenant.SalesPinVersion),
            cancellationToken: cancellationToken);

        return result.MatchedCount == 1;
    }

    public async Task RevokeAllSessionsAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var stillOpen = Builders<Session>.Filter.And(
            Builders<Session>.Filter.Eq(session => session.UserId, userId),
            Builders<Session>.Filter.Eq(session => session.RevokedAt, null));

        await context.Sessions.UpdateManyAsync(
            stillOpen,
            Builders<Session>.Update.Set(session => session.RevokedAt, now),
            new UpdateOptions(),
            cancellationToken);
    }

    private static FilterDefinition<User> ByEmail(EmailAddress email) =>
        Builders<User>.Filter.Eq(user => user.Email, email);
}
