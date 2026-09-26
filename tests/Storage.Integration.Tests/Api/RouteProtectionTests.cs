using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Storage.Api.Endpoints;
using Storage.Integration.Tests.Mongo;

namespace Storage.Integration.Tests.Api;

/// <summary>
/// Which routes are open and which are the owner's, read from the running application. A
/// new route is protected by the fallback policy without anyone remembering to; these
/// tests make every exception to that a deliberate, reviewed line.
/// </summary>
public sealed class RouteProtectionTests(MongoFixture mongo) : IAsyncDisposable
{
    private readonly StorageApiFactory _api = new(mongo);

    [Fact]
    public void Only_the_front_door_is_open_without_signing_in()
    {
        var open = Routes(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null);

        Assert.Equal(
            [
                "GET /health",
                "GET /openapi/{documentName}.json",
                "POST /api/auth/refresh",
                "POST /api/auth/sign-in",
                "POST /api/auth/sign-out",
                "POST /api/auth/sign-up",
            ],
            open);
    }

    [Fact]
    public void Only_the_owner_changes_the_team_settles_a_count_or_sweeps_by_hand()
    {
        var ownerOnly = Routes(endpoint => endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Any(data => data.Policy == TeamEndpoints.OwnerPolicy));

        Assert.Equal(
            [
                "POST /api/counts/{id:guid}/cancel",
                "POST /api/counts/{id:guid}/close",
                "POST /api/stock/expiry-sweep",
                "POST /api/team/",
                "POST /api/team/{id:guid}/activate",
                "POST /api/team/{id:guid}/deactivate",
                "PUT /api/sales/pin",
            ],
            ownerOnly);
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private string[] Routes(Func<RouteEndpoint, bool> predicate) =>
        _api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(predicate)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
