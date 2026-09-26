using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;
using Storage.Infrastructure.Persistence;

namespace Storage.Integration.Tests.Persistence;

/// <summary>
/// Checks what actually lands in a document. None of this needs a running server: the
/// driver serialises to an in-memory BSON document exactly as it would on the wire, so
/// the storage decisions - cents as Int64, barcodes normalised, enums by name - can be
/// verified on every build.
/// </summary>
public sealed class BsonMappingTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    public BsonMappingTests() => StorageBsonSerialization.Register();

    [Fact]
    public void Money_is_written_as_a_64_bit_count_of_cents()
    {
        var document = NewProduct().ToBsonDocument();

        // 899, not 8.99: a double cannot hold 0.10 exactly, an integer sums and sorts exactly.
        Assert.Equal(BsonType.Int64, document["SalePrice"].BsonType);
        Assert.Equal(899L, document["SalePrice"].AsInt64);
    }

    [Fact]
    public void A_barcode_is_written_in_its_normalised_form()
    {
        var packaging = NewProduct().ToBsonDocument()["Packagings"].AsBsonArray[0].AsBsonDocument;

        Assert.Equal("07891000000014", packaging["Gtin"].AsString);
    }

    [Fact]
    public void Enums_are_written_by_name()
    {
        var document = NewProduct().ToBsonDocument();

        Assert.Equal(BsonType.String, document["BaseUnit"].BsonType);
        Assert.Equal("Unit", document["BaseUnit"].AsString);
    }

    [Fact]
    public void Ids_use_the_standard_uuid_representation()
    {
        var document = NewProduct().ToBsonDocument();

        var id = document["_id"].AsBsonBinaryData;
        Assert.Equal(BsonBinarySubType.UuidStandard, id.SubType);
    }

    [Fact]
    public void Every_document_carries_its_shop()
    {
        var product = NewProduct().ToBsonDocument();
        var category = Category.CreateRoot(Tenant, "Bebidas").ToBsonDocument();

        Assert.True(product.Contains("TenantId"));
        Assert.True(category.Contains("TenantId"));
    }

    [Fact]
    public void Derived_values_are_not_stored()
    {
        var document = NewProduct().ToBsonDocument();
        var packaging = document["Packagings"].AsBsonArray[0].AsBsonDocument;

        // Stored copies of computed values would eventually disagree with their source.
        Assert.False(document.Contains("DefaultPackaging"));
        Assert.False(packaging.Contains("IsInternalCode"));
    }

    [Fact]
    public void Timestamps_are_written_as_iso_text()
    {
        var document = NewProduct().ToBsonDocument();

        Assert.Equal(BsonType.String, document["CreatedAt"].BsonType);
    }

    [Fact]
    public void A_product_survives_a_round_trip_with_its_packagings()
    {
        var original = NewProduct();
        original.AddPackaging(Gtin.Parse("17891000000011"), "Fardo 12", conversionFactor: 12);

        var restored = BsonSerializer.Deserialize<Product>(original.ToBsonDocument());

        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.TenantId, restored.TenantId);
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.SalePrice, restored.SalePrice);
        Assert.Equal(UnitOfMeasure.Unit, restored.BaseUnit);
        Assert.Equal(Now, restored.CreatedAt);
        Assert.Equal(2, restored.Packagings.Count);
        Assert.Equal(12, restored.BaseUnitsFor(Gtin.Parse("17891000000011")));
        Assert.Null(restored.DefaultPackaging.Name);
    }

    [Fact]
    public void A_category_survives_a_round_trip_with_its_place_in_the_tree()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");
        var energy = beverages.CreateChild("Energéticos");

        var restored = BsonSerializer.Deserialize<Category>(energy.ToBsonDocument());

        Assert.Equal(energy.Id, restored.Id);
        Assert.Equal(beverages.Id, restored.ParentId);
        Assert.Equal(energy.Path, restored.Path);
        Assert.Equal(1, restored.Depth);
        Assert.True(restored.IsDescendantOf(beverages));
    }

    [Fact]
    public void A_root_category_round_trips_with_no_parent()
    {
        var beverages = Category.CreateRoot(Tenant, "Bebidas");

        var restored = BsonSerializer.Deserialize<Category>(beverages.ToBsonDocument());

        Assert.Null(restored.ParentId);
        Assert.True(restored.IsRoot);
    }

    [Fact]
    public void A_discount_rule_keeps_its_weekdays_by_name_and_round_trips()
    {
        var rule = Storage.Domain.Pricing.DiscountRule.Create(Tenant, "Terça do energético",
            Storage.Domain.Pricing.DiscountType.Percentage, 1500, Storage.Domain.Pricing.DiscountTarget.Category, Guid.CreateVersion7());
        rule.Schedule(new DateOnly(2026, 10, 1), null, [DayOfWeek.Tuesday, DayOfWeek.Thursday]);
        rule.LimitToExpiringWithin(3);

        var document = rule.ToBsonDocument();
        var restored = BsonSerializer.Deserialize<Storage.Domain.Pricing.DiscountRule>(document);

        // A list of enums is not "top level": without the convention reaching into lists,
        // the weekdays would be stored as 2 and 4.
        Assert.Equal(new BsonArray { "Tuesday", "Thursday" }, document["DaysOfWeek"].AsBsonArray);
        Assert.Equal("Percentage", document["Type"].AsString);
        Assert.Equal([DayOfWeek.Tuesday, DayOfWeek.Thursday], restored.DaysOfWeek);
        Assert.Equal(3, restored.ExpiringWithinDays);
        Assert.Equal(new DateOnly(2026, 10, 1), restored.StartsOn);
    }

    private static Product NewProduct()
    {
        var product = Product.Create(
            Tenant,
            "Energético 473ml",
            Guid.CreateVersion7(),
            UnitOfMeasure.Unit,
            Money.FromDecimal(8.99m),
            Gtin.Parse("7891000000014"));

        product.MarkCreated(Now);
        return product;
    }
}
