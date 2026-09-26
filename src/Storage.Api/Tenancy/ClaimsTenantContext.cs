using Storage.Api.Auth;
using Storage.Application.Abstractions;

namespace Storage.Api.Tenancy;

/// <summary>
/// Reads the shop from the authenticated user's token.
/// </summary>
/// <remarks>
/// There is deliberately no fallback: a request without a tenant claim fails instead of
/// defaulting to some shop, because a default is exactly how one customer ends up reading
/// another customer's stock.
/// </remarks>
public sealed class ClaimsTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public Guid TenantId
    {
        get
        {
            var value = accessor.HttpContext?.User.FindFirst(StorageClaims.Tenant)?.Value;

            return Guid.TryParse(value, out var tenantId)
                ? tenantId
                : throw new MissingTenantException();
        }
    }
}

/// <summary>
/// The request reached a shop-scoped operation without saying which shop. Its own type so
/// the API can answer 401 for exactly this, and not for every access-denied the runtime
/// might raise on its own.
/// </summary>
public sealed class MissingTenantException() : Exception("The request carries no tenant.");
