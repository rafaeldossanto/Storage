using Storage.Domain.Common;

namespace Storage.Domain.Tests;

internal static class DomainAssert
{
    /// <summary>
    /// Asserts that <paramref name="action"/> breaks the business rule identified by
    /// <paramref name="code"/> - the code, not the message, is what the API contract carries.
    /// </summary>
    public static void Breaks(string code, Action action) =>
        Assert.Equal(code, Assert.Throws<DomainException>(action).Code);
}
