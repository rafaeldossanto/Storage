using Storage.Domain.Accounts;

namespace Storage.Application.Abstractions;

/// <summary>The people of the current shop.</summary>
public interface IUserRepository
{
    Task<User?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default);

    Task AddAsync(User user, CancellationToken cancellationToken = default);

    Task UpdateAsync(User user, CancellationToken cancellationToken = default);
}
