using Storage.Application.Abstractions;

namespace Storage.Api.Tenancy;

/// <summary>
/// Lets server-side work - a background job visiting every shop - act as one shop at a time.
/// </summary>
/// <remarks>
/// Set only by code running on the server, before it resolves anything shop-scoped in its
/// own service scope. Nothing in a request can reach it: a request's shop always comes from
/// its token.
/// </remarks>
public sealed class TenantOverride
{
    public Guid? TenantId { get; private set; }

    public void ActAs(Guid tenantId)
    {
        if (TenantId is not null && TenantId != tenantId)
        {
            throw new InvalidOperationException("A scope acts as one shop only; create a new scope for another.");
        }

        TenantId = tenantId;
    }
}

/// <summary>The shop a background job is working on.</summary>
public sealed class SystemTenantContext(Guid tenantId) : ITenantContext
{
    public Guid TenantId { get; } = tenantId;
}

public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Every shop-scoped read takes the shop from the signed-in user's token, or - for work
    /// the server does on its own - from <see cref="TenantOverride"/>. There is no default
    /// shop anywhere, in any environment.
    /// </summary>
    public static IServiceCollection AddStorageTenancy(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<TenantOverride>();
        services.AddScoped<ClaimsTenantContext>();

        services.AddScoped<ITenantContext>(provider =>
            provider.GetRequiredService<TenantOverride>().TenantId is { } tenantId
                ? new SystemTenantContext(tenantId)
                : provider.GetRequiredService<ClaimsTenantContext>());

        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<ClaimsTenantContext>());

        return services;
    }
}
