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
    public const string TenantClaim = "tenant_id";

    public Guid TenantId
    {
        get
        {
            var value = accessor.HttpContext?.User.FindFirst(TenantClaim)?.Value;

            return Guid.TryParse(value, out var tenantId)
                ? tenantId
                : throw new UnauthorizedAccessException("The request carries no tenant.");
        }
    }
}
