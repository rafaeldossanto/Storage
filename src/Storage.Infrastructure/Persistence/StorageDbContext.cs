using Microsoft.EntityFrameworkCore;
using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;
using Storage.Infrastructure.Persistence.Conversions;

namespace Storage.Infrastructure.Persistence;

public sealed class StorageDbContext(DbContextOptions<StorageDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<PackagingUnit> PackagingUnits => Set<PackagingUnit>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Money>()
            .HaveConversion<MoneyConverter>();

        configurationBuilder.Properties<Gtin>()
            .HaveConversion<GtinConverter>()
            .HaveMaxLength(Gtin.NormalizedLength)
            .AreFixedLength();

        // Enums are stored by name, not by ordinal: a database someone opens in five years
        // should read EXPIRY_LOSS, not 3, and reordering a member must never silently
        // rewrite history.
        configurationBuilder.Properties<UnitOfMeasure>()
            .HaveConversion<string>()
            .HaveMaxLength(20);

        // DateOnly and TimeOnly are handled natively by the SQLite provider since EF Core 8,
        // so an expiry date needs no converter of its own.
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StorageDbContext).Assembly);
    }
}
