using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Accounts;

namespace Storage.Application.Accounts;

/// <summary>
/// The people of the current shop. Changing the team is reserved to the owner - enforced
/// by the API's authorisation policy, since it is about who calls, not about the data.
/// </summary>
public sealed class TeamService(
    IUserRepository users,
    IAccountStore accounts,
    IPasswordHasher hasher,
    ITenantContext tenant,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<TeamMemberDto>> ListAsync(CancellationToken cancellationToken = default) =>
        (await users.ListAsync(cancellationToken))
            .OrderBy(user => user.Role)
            .ThenBy(user => user.Name, StringComparer.InvariantCultureIgnoreCase)
            .Select(ToDto)
            .ToArray();

    /// <summary>
    /// Creates an employee account with a password the owner sets and hands over. Invites
    /// by e-mail need outgoing mail, which the platform does not send yet.
    /// </summary>
    public async Task<TeamMemberDto> AddStaffAsync(AddStaffRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var email = EmailAddress.Parse(request.Email);
        var password = PasswordPolicy.Require(request.Password);

        // The e-mail is the login, so it is unique across every shop, not just this one.
        if (await accounts.EmailInUseAsync(email, cancellationToken))
        {
            throw UseCaseException.Conflict(ErrorCodes.EmailTaken, "This e-mail already has an account.");
        }

        var staff = User.CreateStaff(tenant.TenantId, request.Name, email, hasher.Hash(password));
        await users.AddAsync(staff, cancellationToken);

        return ToDto(staff);
    }

    /// <summary>
    /// Deactivates someone and signs them out everywhere at once - otherwise a fired
    /// employee would keep working until their session happened to expire.
    /// </summary>
    public async Task<TeamMemberDto> DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await RequireAsync(id, cancellationToken);
        user.Deactivate();

        await users.UpdateAsync(user, cancellationToken);
        await accounts.RevokeAllSessionsAsync(user.Id, clock.GetUtcNow(), cancellationToken);

        return ToDto(user);
    }

    public async Task<TeamMemberDto> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await RequireAsync(id, cancellationToken);
        user.Activate();

        await users.UpdateAsync(user, cancellationToken);
        return ToDto(user);
    }

    private async Task<User> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await users.FindAsync(id, cancellationToken)
        ?? throw UseCaseException.NotFound(ErrorCodes.UserNotFound, $"User {id} does not exist.");

    private static TeamMemberDto ToDto(User user) => new(
        user.Id,
        user.Name,
        user.Email.Value,
        user.Role,
        user.Active,
        user.CreatedAt);
}
