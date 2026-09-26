using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Domain.Accounts;

namespace Storage.Infrastructure.Persistence;

public sealed class MongoTenantDirectory(MongoStorageContext context) : ITenantDirectory
{
    public async Task<IReadOnlyList<Guid>> ListActiveTenantIdsAsync(CancellationToken cancellationToken = default) =>
        await context.Tenants
            .Find(Builders<Tenant>.Filter.Eq(tenant => tenant.Active, true))
            .Project(tenant => tenant.Id)
            .ToListAsync(cancellationToken);
}
