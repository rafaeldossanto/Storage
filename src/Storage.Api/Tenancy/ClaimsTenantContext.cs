using Storage.Api.Auth;
using Storage.Application.Abstractions;

namespace Storage.Api.Tenancy;

/// <summary>
/// Reads the shop and the person from the authenticated user's token.
/// </summary>
/// <remarks>
/// There is deliberately no fallback: a request without a tenant claim fails instead of
/// defaulting to some shop, because a default is exactly how one customer ends up reading
/// another customer's stock.
/// </remarks>
public sealed class ClaimsTenantContext(IHttpContextAccessor accessor) : ITenantContext, ICurrentUser
{
    public Guid TenantId => ReadGuid(StorageClaims.Tenant);

    public Guid UserId => ReadGuid(StorageClaims.Subject);

    private Guid ReadGuid(string claim)
    {
        var value = accessor.HttpContext?.User.FindFirst(claim)?.Value;

        return Guid.TryParse(value, out var id)
            ? id
            : throw new MissingTenantException();
    }
}

/// <summary>
/// The request reached a shop-scoped operation without saying which shop - or who - is
/// asking. Its own type so the API answers 401 for exactly this, and not for every
/// access-denied the runtime might raise on its own.
/// </summary>
public sealed class MissingTenantException() : Exception("The request carries no tenant or no user.");
