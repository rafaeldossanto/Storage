using Storage.Application.Abstractions;

namespace Storage.Api.Tenancy;

/// <summary>
/// A fixed shop for local development, until authentication issues real tokens.
/// </summary>
/// <remarks>
/// Registered only when the environment is Development - see Program.cs. It must never be
/// reachable in production, where it would put every request in the same shop.
/// </remarks>
public sealed class DevelopmentTenantContext(Guid tenantId) : ITenantContext
{
    public Guid TenantId { get; } = tenantId;
}
