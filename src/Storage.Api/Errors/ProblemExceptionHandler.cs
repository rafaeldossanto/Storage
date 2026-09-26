using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Storage.Api.Tenancy;
using Storage.Application.Errors;
using Storage.Application.Stock;
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

    /// <summary>No token, or one that expired: the front end refreshes, then retries.</summary>
    public const string UnauthenticatedCode = "auth.unauthenticated";

    /// <summary>Signed in, but the role does not allow it - staff trying to change the team.</summary>
    public const string ForbiddenCode = "auth.forbidden";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var refusal = exception switch
        {
            UseCaseException { Kind: ErrorKind.NotFound } e => (Status: StatusCodes.Status404NotFound, e.Code),
            UseCaseException { Kind: ErrorKind.Conflict } e => (Status: StatusCodes.Status409Conflict, e.Code),
            UseCaseException { Kind: ErrorKind.Unauthorized } e => (Status: StatusCodes.Status401Unauthorized, e.Code),
            UseCaseException { Kind: ErrorKind.TooManyAttempts } e => (Status: StatusCodes.Status429TooManyRequests, e.Code),
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

        var context = Context(httpContext, answer.Status, answer.Code, exception.Message, exception);

        // Which line of a multi-line document was refused - a goods receipt, a count - so the
        // screen can point at it instead of leaving the person to guess among forty.
        if (exception.Data[ReceivingService.LineKey] is int line)
        {
            context.ProblemDetails.Extensions[ReceivingService.LineKey] = line;
        }

        return await problemDetails.TryWriteAsync(context);
    }

    /// <summary>Writes the same problem shape for refusals that are not exceptions.</summary>
    public static async Task WriteAsync(HttpContext httpContext, int status, string code)
    {
        httpContext.Response.StatusCode = status;

        var service = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await service.WriteAsync(Context(httpContext, status, code, detail: null, exception: null));
    }

    private static ProblemDetailsContext Context(
        HttpContext httpContext,
        int status,
        string code,
        string? detail,
        Exception? exception) => new()
    {
        HttpContext = httpContext,
        Exception = exception,
        ProblemDetails = new ProblemDetails
        {
            Status = status,
            Title = code,
            Detail = detail,
            Extensions = { ["code"] = code },
        },
    };
}
