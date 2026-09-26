using MongoDB.Driver;
using Storage.Application.Abstractions;
using Storage.Domain.Stock;

namespace Storage.Infrastructure.Persistence;

public sealed class SupplierRepository(MongoStorageContext context, ITenantContext tenant) : ISupplierRepository
{
    private FilterDefinition<Supplier> OfThisShop =>
        Builders<Supplier>.Filter.Eq(supplier => supplier.TenantId, tenant.TenantId);

    public async Task<Supplier?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        await context.Suppliers
            .Find(OfThisShop & Builders<Supplier>.Filter.Eq(supplier => supplier.Id, id))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Supplier>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.Suppliers.Find(OfThisShop).ToListAsync(cancellationToken);

    public async Task AddAsync(Supplier supplier, CancellationToken cancellationToken = default)
    {
        Guard(supplier);
        await context.Suppliers.InsertOneAsync(supplier, options: null, cancellationToken);
    }

    public async Task UpdateAsync(Supplier supplier, CancellationToken cancellationToken = default)
    {
        Guard(supplier);

        await context.Suppliers.ReplaceOneAsync(
            OfThisShop & Builders<Supplier>.Filter.Eq(stored => stored.Id, supplier.Id),
            supplier,
            new ReplaceOptions(),
            cancellationToken);
    }

    /// <summary>
    /// Refuses to write a record belonging to another shop, even if a caller hands one over.
    /// </summary>
    private void Guard(Supplier supplier)
    {
        ArgumentNullException.ThrowIfNull(supplier);

        if (supplier.TenantId != tenant.TenantId)
        {
            throw new InvalidOperationException("Attempted to write a supplier that belongs to another tenant.");
        }
    }
}
