using Bipe.Domain.ValueObjects;
using Bipe.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore;

namespace Bipe.Infrastructure.Persistence;

public sealed class BipeDbContext(DbContextOptions<BipeDbContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Money>()
            .HaveConversion<MoneyConverter>();

        configurationBuilder.Properties<Gtin>()
            .HaveConversion<GtinConverter>()
            .HaveMaxLength(Gtin.NormalizedLength)
            .AreFixedLength();

        // DateOnly and TimeOnly are handled natively by the SQLite provider since EF Core 8,
        // so an expiry date needs no converter of its own.
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BipeDbContext).Assembly);
    }
}
