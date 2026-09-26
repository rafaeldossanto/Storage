using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Storage.Api.Tenancy;
using Storage.Application.Errors;
using Storage.Domain.Common;

namespace Storage.Api.Errors;

/// <summary>
/// Answers refused requests with a problem document carrying a stable <c>code</c>.
/// </summary>
/// <remarks>
/// Only the exception types that mean "the request was refused" are handled here. Anything
/// else is a bug and falls through to a plain 500, without its message: an internal error
/// text is not something the browser should ever receive. The front end keys the
/// Portuguese message the shopkeeper reads on <c>code</c>, never on <c>detail</c>.
/// </remarks>
public sealed class ProblemExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public const string NoTenantCode = "auth.no_tenant";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var refusal = exception switch
        {
            UseCaseException { Kind: ErrorKind.NotFound } e => (Status: StatusCodes.Status404NotFound, e.Code),
            UseCaseException { Kind: ErrorKind.Conflict } e => (Status: StatusCodes.Status409Conflict, e.Code),
            UseCaseException e => (Status: StatusCodes.Status422UnprocessableEntity, e.Code),
            DomainException e => (Status: StatusCodes.Status422UnprocessableEntity, e.Code),
            MissingTenantException => (Status: StatusCodes.Status401Unauthorized, Code: NoTenantCode),
            _ => ((int Status, string Code)?)null,
        };

        if (refusal is not { } answer)
        {
            return false;
        }

        httpContext.Response.StatusCode = answer.Status;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = answer.Status,
                Title = answer.Code,
                Detail = exception.Message,
                Extensions = { ["code"] = answer.Code },
            },
        });
    }
}
