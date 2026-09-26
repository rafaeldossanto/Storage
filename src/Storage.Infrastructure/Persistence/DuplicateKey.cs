using MongoDB.Driver;
using Storage.Application.Errors;

namespace Storage.Infrastructure.Persistence;

/// <summary>
/// Turns a unique index violation into the same error the application layer raises.
/// </summary>
/// <remarks>
/// The use cases check for duplicates first and answer clearly in the common case, but two
/// requests can pass that check at the same instant; only the unique index settles it.
/// Whoever loses the race must get the same 409 as if the check had caught it, not a 500.
/// </remarks>
internal static class DuplicateKey
{
    public static async Task GuardAsync(Func<Task> write, string code, string message)
    {
        try
        {
            await write();
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw UseCaseException.Conflict(code, message);
        }
        catch (MongoBulkWriteException exception)
            when (exception.WriteErrors.Any(error => error.Category == ServerErrorCategory.DuplicateKey))
        {
            throw UseCaseException.Conflict(code, message);
        }
    }
}
