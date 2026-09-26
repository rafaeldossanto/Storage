using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Storage.Infrastructure.Persistence;
using Storage.Integration.Tests.Mongo;

namespace Storage.Integration.Tests.Api;

/// <summary>
/// The whole API in memory - routing, authentication, authorisation policies, the error
/// handler - on a database of its own in the test MongoDB.
/// </summary>
/// <remarks>
/// This is what the use-case tests cannot see: which route is open, which one is the
/// owner's, and what a refusal looks like on the wire. Removing a
/// <c>RequireAuthorization</c> breaks a test here, not a shop in production.
/// </remarks>
public sealed class StorageApiFactory(MongoFixture mongo, int authPermitsPerMinute = 1_000)
    : WebApplicationFactory<Program>
{
    private readonly string _database = $"api_{Guid.CreateVersion7():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Jobs:ExpirySweep:Enabled", "false");
        builder.UseSetting(
            "RateLimiting:AuthPermitsPerMinute", authPermitsPerMinute.ToString(CultureInfo.InvariantCulture));

        // Every client of the in-memory server shares one address, so every test would
        // share one rate-limit bucket; the limit is raised unless a test is about it.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<MongoStorageContext>();
            services.AddSingleton(new MongoStorageContext(mongo.Client, _database));
        });
    }
}

/// <summary>Talking to the API the way the front end does.</summary>
internal static class ApiCalls
{
    public const string Password = "senha-forte-123";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A new shop and its owner; returns the owner's access token.</summary>
    public static async Task<string> SignUpAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/sign-up",
            new { shopName = "Mercadinho de Teste", ownerName = "Dona", email, password = Password },
            Token);

        response.EnsureSuccessStatusCode();
        return (await BodyAsync(response)).GetProperty("accessToken").GetString()!;
    }

    public static async Task<string> SignInAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { email, password = Password }, Token);

        response.EnsureSuccessStatusCode();
        return (await BodyAsync(response)).GetProperty("accessToken").GetString()!;
    }

    public static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? accessToken = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request, Token);
    }

    public static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token));

    /// <summary>The stable code of a refusal - what the front end translates.</summary>
    public static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await BodyAsync(response)).TryGetProperty("code", out var code) ? code.GetString() : null;

    public static string NewEmail() => $"{Guid.CreateVersion7():N}@teste.com";
}
