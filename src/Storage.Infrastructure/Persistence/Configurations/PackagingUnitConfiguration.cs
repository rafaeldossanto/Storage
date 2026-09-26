using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storage.Domain.Catalog;

namespace Storage.Infrastructure.Persistence.Configurations;

public sealed class PackagingUnitConfiguration : IEntityTypeConfiguration<PackagingUnit>
{
    public void Configure(EntityTypeBuilder<PackagingUnit> builder)
    {
        builder.ToTable("PackagingUnits");

        builder.HasKey(packaging => packaging.Id);

        builder.Property(packaging => packaging.Gtin)
            .IsRequired();

        builder.Property(packaging => packaging.Name)
            .HasMaxLength(PackagingUnit.NameMaxLength);

        builder.Property(packaging => packaging.ConversionFactor)
            .IsRequired();

        // One barcode points at exactly one thing in the shop. Without this, a mistyped
        // code at registration would silently hijack another product at the counter.
        builder.HasIndex(packaging => packaging.Gtin)
            .IsUnique();

        // A partial index: at most one default packaging per product, enforced by the
        // database rather than by whoever remembers to check.
        builder.HasIndex(packaging => new { packaging.ProductId, packaging.IsDefault })
            .IsUnique()
            .HasFilter("\"IsDefault\" = 1");

        builder.Ignore(packaging => packaging.IsInternalCode);
    }
}
