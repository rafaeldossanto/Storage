namespace Storage.Api.Auth;

/// <summary>
/// The refresh token travels only in this cookie.
/// </summary>
/// <remarks>
/// HttpOnly, so no script on the page - including one injected by an attacker - can read
/// it. SameSite=Strict, so another site cannot make the browser send it. Path-limited to
/// the auth routes, so it is not attached to every request. The short-lived access token
/// lives in the page's memory instead; stealing it buys at most fifteen minutes.
/// </remarks>
internal static class RefreshCookie
{
    public const string Name = "storage_refresh";
    public const string Path = "/api/auth";

    public static void Write(HttpResponse response, string token, DateTimeOffset expires, AuthSettings settings) =>
        response.Cookies.Append(Name, token, Options(settings, expires));

    public static string? Read(HttpRequest request) => request.Cookies[Name];

    public static void Clear(HttpResponse response, AuthSettings settings) =>
        response.Cookies.Delete(Name, Options(settings, expires: null));

    private static CookieOptions Options(AuthSettings settings, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = settings.SecureCookies,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        Expires = expires,
        IsEssential = true,
    };
}
