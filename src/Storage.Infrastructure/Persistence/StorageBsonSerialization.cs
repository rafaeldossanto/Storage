using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using Storage.Domain.Accounts;
using Storage.Domain.Catalog;
using Storage.Domain.Pricing;
using Storage.Domain.Stock;
using Storage.Domain.ValueObjects;

namespace Storage.Infrastructure.Persistence;

/// <summary>
/// Teaches the MongoDB driver how to read and write the domain types.
/// </summary>
/// <remarks>
/// The mapping lives here, not on the entities: no BSON attribute ever reaches
/// <c>Storage.Domain</c>, which is what keeps the domain free of any knowledge about
/// where it is stored.
/// </remarks>
public static class StorageBsonSerialization
{
    private static bool _registered;
    private static readonly Lock Gate = new();

    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            // Enums by name, not by ordinal: a document read in five years should say
            // "ExpiryLoss", not 2, and reordering an enum must never rewrite history. A
            // convention only affects class maps built after it, so it goes first. Not only
            // top-level: a rule's weekdays are a list of enums and must read "Tuesday" too.
            ConventionRegistry.Register(
                "storage-domain",
                new ConventionPack { new EnumRepresentationConvention(BsonType.String, topLevelOnly: false) },
                type => type.Namespace?.StartsWith("Storage.Domain", StringComparison.Ordinal) == true);

            // Standard UUID binary (subtype 4), the only representation that is portable
            // across drivers - the legacy subtype 3 byte order differs per language.
            BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

            // ISO-8601 text: readable in Compass, and since everything is written in UTC
            // it also sorts chronologically, which date range reports depend on.
            BsonSerializer.TryRegisterSerializer(new DateTimeOffsetSerializer(BsonType.String));

            BsonSerializer.TryRegisterSerializer(new MoneySerializer());
            BsonSerializer.TryRegisterSerializer(new GtinSerializer());
            BsonSerializer.TryRegisterSerializer(new EmailAddressSerializer());

            RegisterCategory();
            RegisterProduct();
            RegisterPackagingUnit();
            RegisterTenant();
            RegisterUser();
            RegisterSession();
            RegisterBatch();
            RegisterStockMovement();
            RegisterSupplier();
            RegisterGoodsReceipt();
            RegisterDiscountRule();
            RegisterStockCount();

            _registered = true;
        }
    }

    private static void RegisterBatch() =>
        BsonClassMap.TryRegisterClassMap<Batch>(map =>
        {
            map.AutoMap();
            map.MapIdProperty(batch => batch.Id);
            map.SetIgnoreExtraElements(true);
        });

    private static void RegisterStockMovement() =>
        BsonClassMap.TryRegisterClassMap<StockMovement>(map =>
        {
            map.AutoMap();
            map.MapIdProperty(movement => movement.Id);
            map.SetIgnoreExtraElements(true);
        });

    private static void RegisterSupplier() =>
        BsonClassMap.TryRegisterClassMap<Supplier>(map =>
        {
            map.AutoMap();
            map.MapIdProperty(supplier => supplier.Id);
            map.SetIgnoreExtraElements(true);
        });

    private static void RegisterGoodsReceipt()
    {
        BsonClassMap.TryRegisterClassMap<GoodsReceipt>(map =>
        {
            map.AutoMap();
            map.MapIdProperty(receipt => receipt.Id);
            map.SetIgnoreExtraElements(true);

            // Lines are exposed read-only; the backing field is what gets stored.
            map.UnmapProperty(receipt => receipt.Lines);
            map.UnmapProperty(receipt => receipt.TotalCost);
            map.MapField("_lines").SetElementName("Lines");
        });

        BsonClassMap.TryRegisterClassMap<ReceiptLine>(map =>
        {
            map.AutoMap();
            map.SetIgnoreExtraElements(true);

            // Derived: quantity times the packaging cost.
            map.UnmapProperty(line => line.Total);
        });
    }

    private static void RegisterDiscountRule() =>
        BsonClassMap.TryRegisterClassMap<DiscountRule>(map =>
        {
            map.AutoMap();
            map.MapIdProperty(rule => rule.Id);
            map.SetIgnoreExtraElements(true);
        });

    private static void RegisterStockCount()
    {
        BsonClassMap.TryRegisterClassMap<StockCount>(map =>
        {
            map.AutoMap();
            map.MapIdProperty(count => count.Id);
            map.SetIgnoreExtraElements(true);

            // Items are exposed read-only; the backing field is what gets stored.
            map.UnmapProperty(count => count.Items);
            map.MapField("_items").SetElementName("Items");
        });

        BsonClassMap.TryRegisterClassMap<CountedItem>(map =>
        {
            map.AutoMap();
            map.SetIgnoreExtraElements(true);
        });
    }

    private static void RegisterTenant() =>
        BsonClassMap.TryRegisterClassMap<Tenant>(map =>
        {
            map.AutoMap();
            map.MapIdProperty(tenant => tenant.Id);
            map.SetIgnoreExtraElements(true);
        });

    private static void RegisterUser() =>
        BsonClassMap.TryRegisterClassMap<User>(map =>
        {
            map.AutoMap();
            map.MapIdProperty(user => user.Id);
            map.SetIgnoreExtraElements(true);
        });

    private static void RegisterSession() =>
        BsonClassMap.TryRegisterClassMap<Session>(map =>
        {
            map.AutoMap();
            map.MapIdProperty(session => session.Id);
            map.SetIgnoreExtraElements(true);

            // A real BSON date, unlike every other timestamp here: the TTL index that
            // deletes expired sessions only understands dates. Sessions are the one thing
            // that may simply disappear - they are plumbing, not business records.
            map.MapMember(session => session.ExpiresAt)
                .SetSerializer(new DateTimeOffsetSerializer(BsonType.DateTime));
        });

    private static void RegisterCategory() =>
        BsonClassMap.TryRegisterClassMap<Category>(map =>
        {
            map.AutoMap();
            map.MapIdProperty(category => category.Id);
            map.SetIgnoreExtraElements(true);
        });

    private static void RegisterProduct() =>
        BsonClassMap.TryRegisterClassMap<Product>(map =>
        {
            map.AutoMap();
            map.MapIdProperty(product => product.Id);
            map.SetIgnoreExtraElements(true);

            // Packagings are exposed read-only, so the backing field is what gets stored.
            // They are embedded in the product document rather than kept in a collection of
            // their own: a product and its barcodes are read, written and versioned together.
            map.UnmapProperty(product => product.Packagings);
            map.UnmapProperty(product => product.DefaultPackaging);
            map.MapField("_packagings").SetElementName("Packagings");
        });

    private static void RegisterPackagingUnit() =>
        BsonClassMap.TryRegisterClassMap<PackagingUnit>(map =>
        {
            map.AutoMap();
            map.SetIgnoreExtraElements(true);

            // Derived from the barcode itself; storing it would let the two disagree.
            map.UnmapProperty(packaging => packaging.IsInternalCode);
        });

    /// <summary>
    /// Money is stored as a 64-bit count of cents.
    /// </summary>
    /// <remarks>
    /// BSON's <c>double</c> cannot hold 0.10 exactly and its <c>decimal128</c> would cost a
    /// conversion on every read. An integer keeps sums exact and sorts correctly in an
    /// aggregation pipeline.
    /// </remarks>
    private sealed class MoneySerializer : StructSerializerBase<Money>
    {
        public override void Serialize(
            BsonSerializationContext context,
            BsonSerializationArgs args,
            Money value) => context.Writer.WriteInt64(value.Cents);

        public override Money Deserialize(
            BsonDeserializationContext context,
            BsonDeserializationArgs args) =>
            Money.FromCents(context.Reader.CurrentBsonType switch
            {
                BsonType.Int64 => context.Reader.ReadInt64(),
                BsonType.Int32 => context.Reader.ReadInt32(),
                var other => throw new FormatException(
                    $"Cannot read Money from BSON type {other}; expected an integer of cents."),
            });
    }

    /// <summary>
    /// A barcode is stored as its normalised fourteen digit string, so an index lookup is
    /// a plain equality match whatever length was printed on the package.
    /// </summary>
    private sealed class GtinSerializer : StructSerializerBase<Gtin>
    {
        public override void Serialize(
            BsonSerializationContext context,
            BsonSerializationArgs args,
            Gtin value) => context.Writer.WriteString(value.Value);

        public override Gtin Deserialize(
            BsonDeserializationContext context,
            BsonDeserializationArgs args) => Gtin.Parse(context.Reader.ReadString());
    }

    /// <summary>
    /// Stored already normalised, so the unique index compares addresses the same way the
    /// application does.
    /// </summary>
    private sealed class EmailAddressSerializer : StructSerializerBase<EmailAddress>
    {
        public override void Serialize(
            BsonSerializationContext context,
            BsonSerializationArgs args,
            EmailAddress value) => context.Writer.WriteString(value.Value);

        public override EmailAddress Deserialize(
            BsonDeserializationContext context,
            BsonDeserializationArgs args) => EmailAddress.Parse(context.Reader.ReadString());
    }
}
