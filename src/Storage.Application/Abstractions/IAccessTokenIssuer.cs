using Storage.Domain.Accounts;

namespace Storage.Application.Abstractions;

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues the short-lived token that authenticates each request. It carries the shop, so
/// every shop-scoped read after sign-in knows whose data it may touch.
/// </summary>
public interface IAccessTokenIssuer
{
    AccessToken Issue(User user);
}
