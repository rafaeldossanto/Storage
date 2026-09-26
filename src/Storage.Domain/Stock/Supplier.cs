using Storage.Domain.Common;

namespace Storage.Domain.Stock;

/// <summary>Who delivers the goods.</summary>
public sealed class Supplier : ITenantScoped
{
    public const int NameMaxLength = 80;
    public const int TaxIdMaxLength = 20;
    public const int ContactMaxLength = 120;

    private Supplier()
    {
        // Driver materialisation.
    }

    private Supplier(Guid tenantId, string name, string? taxId, string? contact)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Name = ValidateName(name);
        TaxId = Optional(taxId, TaxIdMaxLength);
        Contact = Optional(contact, ContactMaxLength);
        Active = true;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// The CNPJ or CPF as typed, without check-digit validation on purpose: since July 2026
    /// the CNPJ can contain letters, and a digits-only rule would turn away every supplier
    /// registered under the new format.
    /// </summary>
    public string? TaxId { get; private set; }

    /// <summary>Phone, e-mail or the sales rep's name - whatever the shop uses to reorder.</summary>
    public string? Contact { get; private set; }

    public bool Active { get; private set; }

    public static Supplier Create(Guid tenantId, string name, string? taxId = null, string? contact = null) =>
        new(tenantId, name, taxId, contact);

    public void Update(string name, string? taxId, string? contact)
    {
        Name = ValidateName(name);
        TaxId = Optional(taxId, TaxIdMaxLength);
        Contact = Optional(contact, ContactMaxLength);
    }

    public void Activate() => Active = true;

    /// <summary>Hides the supplier from new receipts; past receipts still name it.</summary>
    public void Deactivate() => Active = false;

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > NameMaxLength)
        {
            throw new DomainException(
                DomainErrors.SupplierNameInvalid,
                $"A supplier name is required and limited to {NameMaxLength} characters.");
        }

        return trimmed;
    }

    private static string? Optional(string? value, int maxLength)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maxLength
            ? trimmed
            : throw new DomainException(
                DomainErrors.SupplierFieldTooLong, $"This field is limited to {maxLength} characters.");
    }
}
