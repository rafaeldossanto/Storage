using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storage.Domain.Catalog;

namespace Storage.Infrastructure.Persistence.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");

        builder.HasKey(product => product.Id);

        builder.Property(product => product.Name)
            .IsRequired()
            .HasMaxLength(Product.NameMaxLength);

        builder.Property(product => product.SalePrice)
            .IsRequired();

        builder.Property(product => product.AverageCost)
            .IsRequired();

        builder.Property(product => product.BaseUnit)
            .IsRequired();

        builder.HasIndex(product => product.CategoryId);

        builder.HasIndex(product => product.Name);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(product => product.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // The collection is exposed as read-only, so EF reads and writes the backing field
        // instead of going through the property.
        builder.Metadata
            .FindNavigation(nameof(Product.Packagings))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(product => product.Packagings)
            .WithOne()
            .HasForeignKey(packaging => packaging.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
