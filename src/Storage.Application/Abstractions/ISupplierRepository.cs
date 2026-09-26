using Storage.Domain.Stock;

namespace Storage.Application.Abstractions;

/// <summary>The suppliers of the current shop.</summary>
public interface ISupplierRepository
{
    Task<Supplier?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Supplier>> ListAsync(CancellationToken cancellationToken = default);

    Task AddAsync(Supplier supplier, CancellationToken cancellationToken = default);

    Task UpdateAsync(Supplier supplier, CancellationToken cancellationToken = default);
}
