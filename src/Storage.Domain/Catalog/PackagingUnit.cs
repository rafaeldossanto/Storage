using Storage.Domain.Common;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Catalog;

/// <summary>
/// One barcode a product answers to, and how many base units it carries.
/// </summary>
/// <remarks>
/// A can and a twelve-pack of the same drink carry different barcodes. Scanning the pack
/// has to move twelve units of stock, not one, so the factor lives next to the code
/// instead of being typed by the operator at the counter.
/// </remarks>
public sealed class PackagingUnit
{
    public const int NameMaxLength = 40;

    private PackagingUnit()
    {
        // EF Core materialisation.
    }

    internal PackagingUnit(Guid productId, Gtin gtin, string? name, int conversionFactor, bool isDefault)
    {
        if (conversionFactor < 1)
        {
            throw new DomainException(
                DomainErrors.PackagingFactorInvalid,
                "A packaging holds at least one base unit.");
        }

        // Only the product itself creates a default packaging, always with factor 1, so
        // breaking this is a bug in the code rather than a rule the user broke.
        if (isDefault && conversionFactor != 1)
        {
            throw new ArgumentException(
                "The default packaging is the base unit and always carries a factor of 1.",
                nameof(conversionFactor));
        }

        Id = Guid.CreateVersion7();
        ProductId = productId;
        Gtin = gtin;
        Name = Normalize(name);
        ConversionFactor = conversionFactor;
        IsDefault = isDefault;
    }

    public Guid Id { get; private set; }

    public Guid ProductId { get; private set; }

    public Gtin Gtin { get; private set; }

    /// <summary>
    /// What the shopkeeper calls this packaging. Null on the default packaging, which has
    /// no name of its own: it is the product itself.
    /// </summary>
    public string? Name { get; private set; }

    public int ConversionFactor { get; private set; }

    public bool IsDefault { get; private set; }

    /// <summary>
    /// True when the code was minted by the shop rather than by a manufacturer, which is
    /// also the range scales print on weighed goods.
    /// </summary>
    public bool IsInternalCode => Gtin.IsInternal;

    public void Rename(string? name) => Name = Normalize(name);

    private static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();

        return trimmed.Length <= NameMaxLength
            ? trimmed
            : throw new DomainException(
                DomainErrors.PackagingNameInvalid,
                $"A packaging name is limited to {NameMaxLength} characters.");
    }
}
