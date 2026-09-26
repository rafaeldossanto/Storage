using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Accounts;

namespace Storage.Infrastructure.Persistence;

public sealed class UserRepository(
    MongoStorageContext context,
    ITenantContext tenant,
    TimeProvider clock) : IUserRepository
{
    private FilterDefinition<User> OfThisShop =>
        Builders<User>.Filter.Eq(user => user.TenantId, tenant.TenantId);

    public async Task<User?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        await context.Users
            .Find(Builders<User>.Filter.And(OfThisShop, Builders<User>.Filter.Eq(user => user.Id, id)))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.Users.Find(OfThisShop).ToListAsync(cancellationToken);

    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        Guard(user);

        user.MarkCreated(clock.GetUtcNow());

        await DuplicateKey.GuardAsync(
            () => context.Users.InsertOneAsync(user, options: null, cancellationToken),
            ErrorCodes.EmailTaken,
            "This e-mail already has an account.");
    }

    public async Task UpdateAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        Guard(user);

        user.MarkUpdated(clock.GetUtcNow());

        await context.Users.ReplaceOneAsync(
            Builders<User>.Filter.And(OfThisShop, Builders<User>.Filter.Eq(stored => stored.Id, user.Id)),
            user,
            new ReplaceOptions(),
            cancellationToken);
    }

    /// <summary>
    /// Refuses to write a record belonging to another shop, even if a caller hands one over.
    /// </summary>
    private void Guard(User user)
    {
        if (user.TenantId != tenant.TenantId)
        {
            throw new InvalidOperationException("Attempted to write a user that belongs to another tenant.");
        }
    }
}
