using Storage.Application.Errors;

namespace Storage.Application.Tests;

internal static class Refused
{
    /// <summary>
    /// Asserts the use case refused the request with this kind and code - the pair the API
    /// turns into a status and the front end turns into a message.
    /// </summary>
    public static async Task WithAsync(ErrorKind kind, string code, Func<Task> action)
    {
        var refusal = await Assert.ThrowsAsync<UseCaseException>(action);

        Assert.Equal(kind, refusal.Kind);
        Assert.Equal(code, refusal.Code);
    }
}
