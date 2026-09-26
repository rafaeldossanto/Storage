using Storage.Application.Accounts;

namespace Storage.Api.Endpoints;

public static class TeamEndpoints
{
    public const string OwnerPolicy = "owner";

    public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder app)
    {
        var team = app.MapGroup("/api/team").WithTags("Team");

        // Anyone in the shop may see who else works there.
        team.MapGet("/", (TeamService service, CancellationToken cancellationToken) =>
            service.ListAsync(cancellationToken));

        // Changing the team is the owner's call.
        team.MapPost("/", async (
            AddStaffRequest request,
            TeamService service,
            CancellationToken cancellationToken) =>
        {
            var added = await service.AddStaffAsync(request, cancellationToken);
            return TypedResults.Created($"/api/team/{added.Id}", added);
        })
        .RequireAuthorization(OwnerPolicy);

        team.MapPost("/{id:guid}/deactivate", (
            Guid id,
            TeamService service,
            CancellationToken cancellationToken) =>
            service.DeactivateAsync(id, cancellationToken))
        .RequireAuthorization(OwnerPolicy);

        team.MapPost("/{id:guid}/activate", (
            Guid id,
            TeamService service,
            CancellationToken cancellationToken) =>
            service.ActivateAsync(id, cancellationToken))
        .RequireAuthorization(OwnerPolicy);

        return app;
    }
}
