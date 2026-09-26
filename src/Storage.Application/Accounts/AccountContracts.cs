using Storage.Application.Abstractions;
using Storage.Domain.Accounts;

namespace Storage.Application.Accounts;

public sealed record SignUpRequest(
    string ShopName,
    string OwnerName,
    string Email,
    string Password,
    string? TimeZone = null);

public sealed record SignInRequest(string Email, string Password);

public sealed record AccountDto(
    Guid UserId,
    string Name,
    string Email,
    UserRole Role,
    Guid ShopId,
    string ShopName,
    string ShopTimeZone);

/// <summary>
/// What a successful sign-in, sign-up or refresh produces. The API sends the access token
/// in the body and the refresh token only as an HttpOnly cookie, where scripts on the page
/// cannot read it.
/// </summary>
public sealed record AuthResult(
    AccessToken AccessToken,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    AccountDto Account);

public sealed record TeamMemberDto(
    Guid Id,
    string Name,
    string Email,
    UserRole Role,
    bool Active,
    DateTimeOffset CreatedAt);

public sealed record AddStaffRequest(string Name, string Email, string Password);

/// <summary>How long a signed-in device stays signed in without being used.</summary>
public sealed record SessionPolicy(TimeSpan RefreshTokenLifetime);
