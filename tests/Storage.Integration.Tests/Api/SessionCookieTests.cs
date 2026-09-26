using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Storage.Integration.Tests.Mongo;
using static Storage.Integration.Tests.Api.ApiCalls;

namespace Storage.Integration.Tests.Api;

/// <summary>
/// The refresh token over HTTP: where it travels, what protects it, and what happens when
/// it comes back after being used.
/// </summary>
public sealed class SessionCookieTests(MongoFixture mongo) : IAsyncDisposable
{
    private const string Cookie = "storage_refresh";

    private readonly StorageApiFactory _api = new(mongo);

    [Fact]
    public async Task The_refresh_token_travels_only_in_a_cookie_scripts_cannot_read()
    {
        var response = await SendAsync(_api.CreateClient(), HttpMethod.Post, "/api/auth/sign-up",
            body: new { shopName = "Mercadinho", ownerName = "Dona", email = NewEmail(), password = Password });

        var body = await BodyAsync(response);
        Assert.True(body.TryGetProperty("accessToken", out _));
        Assert.False(body.TryGetProperty("refreshToken", out _));

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), header => header.StartsWith($"{Cookie}=", StringComparison.Ordinal))
            .ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/api/auth", cookie);
    }

    [Fact]
    public async Task The_cookie_renews_the_session_and_a_replayed_one_ends_it()
    {
        // Two clients: the browser, which keeps its cookies, and whoever copied the first one.
        var browser = _api.CreateClient();
        var copier = _api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var signUp = await SendAsync(browser, HttpMethod.Post, "/api/auth/sign-up",
            body: new { shopName = "Mercadinho", ownerName = "Dona", email = NewEmail(), password = Password });
        var copied = CookieOf(signUp);

        var renewed = await SendAsync(browser, HttpMethod.Post, "/api/auth/refresh");
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);

        // The copy is the token the browser just retired.
        var replay = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        replay.Headers.Add("Cookie", $"{Cookie}={copied}");
        var replayed = await copier.SendAsync(replay, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Equal("auth.session_invalid", await CodeAsync(replayed));

        // Nobody can tell the owner from the copier, so the browser is signed out too.
        var afterwards = await SendAsync(browser, HttpMethod.Post, "/api/auth/refresh");
        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private static string CookieOf(HttpResponseMessage response)
    {
        var header = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith($"{Cookie}=", StringComparison.Ordinal));
        return header[(Cookie.Length + 1)..header.IndexOf(';', StringComparison.Ordinal)];
    }
}
