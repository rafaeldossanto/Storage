using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Domain.Accounts;

namespace Storage.Infrastructure.Persistence;

/// <summary>Reads the current shop's time zone once per request and answers "today" in it.</summary>
public sealed class ShopCalendar(MongoStorageContext context, ITenantContext tenant, TimeProvider clock) : IShopCalendar
{
    private Tenant? _shop;

    public async Task<DateOnly> TodayAsync(CancellationToken cancellationToken = default)
    {
        _shop ??= await context.Tenants
            .Find(Builders<Tenant>.Filter.Eq(shop => shop.Id, tenant.TenantId))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Tenant {tenant.TenantId} does not exist.");

        return _shop.LocalDate(clock.GetUtcNow());
    }
}
