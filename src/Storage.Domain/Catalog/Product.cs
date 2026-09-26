using Storage.Domain.Common;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Catalog;

/// <summary>
/// Something the shop sells, counted in one base unit and reachable by one or more barcodes.
/// </summary>
public sealed class Product : ITimestamped, ITenantScoped
{
    public const int NameMaxLength = 120;

    // Not readonly on purpose: the MongoDB driver fills this field when reading a product,
    // and it silently skips readonly fields - every product would come back with no
    // barcodes. BsonMappingTests guards the round trip.
    private List<PackagingUnit> _packagings = [];

    private Product()
    {
        // EF Core materialisation.
    }

    private Product(
        Guid tenantId,
        string name,
        Guid categoryId,
        UnitOfMeasure baseUnit,
        Money salePrice,
        Gtin gtin)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Name = Validate(name);
        CategoryId = categoryId;
        BaseUnit = baseUnit;
        SalePrice = Positive(salePrice);
        AverageCost = Money.Zero;
        MinimumStock = 0;
        TracksExpiry = true;
        Active = true;

        _packagings.Add(new PackagingUnit(Id, gtin, name: null, conversionFactor: 1, isDefault: true));
    }

    public Guid Id { get; private set; }

    /// <summary>The shop this product belongs to.</summary>
    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public Guid CategoryId { get; private set; }

    public UnitOfMeasure BaseUnit { get; private set; }

    public Money SalePrice { get; private set; }

    /// <summary>
    /// Weighted average of what was paid for the units currently in stock. Written by
    /// goods receiving, never typed by hand, and what the margin report is measured against.
    /// </summary>
    public Money AverageCost { get; private set; }

    public int MinimumStock { get; private set; }

    /// <summary>
    /// Whether receiving asks for an expiry date. False for things that do not spoil, so
    /// the operator is not forced to invent a date for a box of nails.
    /// </summary>
    public bool TracksExpiry { get; private set; }

    public bool Active { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyCollection<PackagingUnit> Packagings => _packagings.AsReadOnly();

    public PackagingUnit DefaultPackaging => _packagings.Single(packaging => packaging.IsDefault);

    /// <summary>
    /// Registers a product with the barcode that was just scanned as its base unit.
    /// </summary>
    public static Product Create(
        Guid tenantId,
        string name,
        Guid categoryId,
        UnitOfMeasure baseUnit,
        Money salePrice,
        Gtin gtin) => new(tenantId, name, categoryId, baseUnit, salePrice, gtin);

    public PackagingUnit AddPackaging(Gtin gtin, string? name, int conversionFactor)
    {
        if (_packagings.Any(packaging => packaging.Gtin == gtin))
        {
            throw new InvalidOperationException(
                $"Product '{Name}' already answers to barcode {gtin.ToDisplay()}.");
        }

        var packaging = new PackagingUnit(Id, gtin, name, conversionFactor, isDefault: false);
        _packagings.Add(packaging);

        return packaging;
    }

    public void RemovePackaging(Guid packagingId)
    {
        var packaging = _packagings.SingleOrDefault(candidate => candidate.Id == packagingId)
            ?? throw new InvalidOperationException("Packaging not found on this product.");

        if (packaging.IsDefault)
        {
            throw new InvalidOperationException(
                "The base unit packaging cannot be removed: stock is counted in it.");
        }

        _packagings.Remove(packaging);
    }

    public PackagingUnit? FindPackaging(Gtin gtin) =>
        _packagings.SingleOrDefault(packaging => packaging.Gtin == gtin);

    /// <summary>
    /// How many base units a scan of this barcode moves.
    /// </summary>
    public int BaseUnitsFor(Gtin gtin) =>
        FindPackaging(gtin)?.ConversionFactor
        ?? throw new InvalidOperationException(
            $"Barcode {gtin.ToDisplay()} does not belong to product '{Name}'.");

    public void Rename(string name) => Name = Validate(name);

    public void ChangePrice(Money salePrice) => SalePrice = Positive(salePrice);

    public void MoveToCategory(Guid categoryId) => CategoryId = categoryId;

    public void SetMinimumStock(int minimumStock)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumStock);
        MinimumStock = minimumStock;
    }

    public void SetExpiryTracking(bool tracksExpiry) => TracksExpiry = tracksExpiry;

    /// <summary>Called by goods receiving once the new weighted average is known.</summary>
    public void UpdateAverageCost(Money averageCost)
    {
        if (averageCost.IsNegative)
        {
            throw new ArgumentException("Cost cannot be negative.", nameof(averageCost));
        }

        AverageCost = averageCost;
    }

    /// <summary>Stamped by the repository from the injected clock, never read from the entity.</summary>
    public void MarkCreated(DateTimeOffset at)
    {
        CreatedAt = at;
        UpdatedAt = at;
    }

    public void MarkUpdated(DateTimeOffset at) => UpdatedAt = at;

    public void Activate() => Active = true;

    /// <summary>
    /// Takes the product out of the counter's reach without deleting it, because past
    /// sales and stock movements still point at it.
    /// </summary>
    public void Deactivate() => Active = false;

    private static Money Positive(Money salePrice) =>
        salePrice.IsNegative
            ? throw new ArgumentException("A sale price cannot be negative.", nameof(salePrice))
            : salePrice;

    private static string Validate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();

        return trimmed.Length <= NameMaxLength
            ? trimmed
            : throw new ArgumentException(
                $"A product name is limited to {NameMaxLength} characters.", nameof(name));
    }
}
