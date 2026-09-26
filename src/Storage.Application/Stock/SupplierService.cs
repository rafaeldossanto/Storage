using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Stock;

namespace Storage.Application.Stock;

public sealed record SupplierDto(Guid Id, string Name, string? TaxId, string? Contact, bool Active);

public sealed record SaveSupplierRequest(string Name, string? TaxId = null, string? Contact = null);

public sealed class SupplierService(ISupplierRepository suppliers, ITenantContext tenant)
{
    public async Task<IReadOnlyList<SupplierDto>> ListAsync(CancellationToken cancellationToken = default) =>
        (await suppliers.ListAsync(cancellationToken))
            .OrderByDescending(supplier => supplier.Active)
            .ThenBy(supplier => supplier.Name, StringComparer.InvariantCultureIgnoreCase)
            .Select(ToDto)
            .ToArray();

    public async Task<SupplierDto> CreateAsync(SaveSupplierRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var supplier = Supplier.Create(tenant.TenantId, request.Name, request.TaxId, request.Contact);
        await suppliers.AddAsync(supplier, cancellationToken);

        return ToDto(supplier);
    }

    public async Task<SupplierDto> UpdateAsync(
        Guid id,
        SaveSupplierRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var supplier = await RequireAsync(id, cancellationToken);
        supplier.Update(request.Name, request.TaxId, request.Contact);

        await suppliers.UpdateAsync(supplier, cancellationToken);
        return ToDto(supplier);
    }

    public async Task<SupplierDto> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var supplier = await RequireAsync(id, cancellationToken);
        supplier.Activate();

        await suppliers.UpdateAsync(supplier, cancellationToken);
        return ToDto(supplier);
    }

    public async Task<SupplierDto> DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var supplier = await RequireAsync(id, cancellationToken);
        supplier.Deactivate();

        await suppliers.UpdateAsync(supplier, cancellationToken);
        return ToDto(supplier);
    }

    private async Task<Supplier> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await suppliers.FindAsync(id, cancellationToken)
        ?? throw UseCaseException.NotFound(ErrorCodes.SupplierNotFound, $"Supplier {id} does not exist.");

    internal static SupplierDto ToDto(Supplier supplier) =>
        new(supplier.Id, supplier.Name, supplier.TaxId, supplier.Contact, supplier.Active);
}
