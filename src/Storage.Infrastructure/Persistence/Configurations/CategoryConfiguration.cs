using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storage.Domain.Catalog;

namespace Storage.Infrastructure.Persistence.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");

        builder.HasKey(category => category.Id);

        builder.Property(category => category.Name)
            .IsRequired()
            .HasMaxLength(Category.NameMaxLength);

        // Long enough for a tree far deeper than any shop will build: each level costs
        // 33 characters.
        builder.Property(category => category.Path)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(category => category.Active)
            .IsRequired();

        // The index that answers "everything under Beverages" as a prefix scan.
        builder.HasIndex(category => category.Path);

        builder.HasIndex(category => new { category.ParentId, category.Name })
            .IsUnique();

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(category => category.ParentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
