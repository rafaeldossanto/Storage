using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Storage.Application.Abstractions;
using Storage.Domain.ValueObjects;
using Storage.Integration.Tests.Mongo;
using static Storage.Integration.Tests.Api.ApiCalls;

namespace Storage.Integration.Tests.Api;

/// <summary>
/// A product's photo over HTTP: the address the product carries, and the one open route
/// that serves the picture to an &lt;img&gt; tag.
/// </summary>
public sealed class ProductPhotoRouteTests(MongoFixture mongo) : IAsyncDisposable
{
    private const string Coke = "7894900010015";

    private readonly StorageApiFactory _api = new(mongo);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_product_carries_its_photo_once_the_worker_has_stored_it()
    {
        var client = _api.CreateClient();
        var owner = await SignUpAsync(client, NewEmail());

        var created = await BodyAsync(await CreateCokeAsync(client, owner));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, created.GetProperty("photo").ValueKind);

        await StorePhotoAsync("v1", [1, 2, 3]);

        var found = await BodyAsync(await SendAsync(client, HttpMethod.Get, $"/api/products/by-barcode/{Coke}", owner));
        var photo = found.GetProperty("photo");
        Assert.Equal($"/api/product-photos/0{Coke}?v=v1", photo.GetProperty("url").GetString());
        Assert.Equal("Open Food Facts", photo.GetProperty("source").GetString());
    }

    [Fact]
    public async Task The_picture_needs_no_sign_in_and_is_cached_for_good_under_its_version()
    {
        await StorePhotoAsync("v1", [1, 2, 3]);
        var client = _api.CreateClient();

        var response = await client.GetAsync($"/api/product-photos/0{Coke}?v=v1", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal([1, 2, 3], await response.Content.ReadAsByteArrayAsync(Token));
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
        Assert.Equal("\"v1\"", response.Headers.ETag?.Tag);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
    }

    [Fact]
    public async Task An_address_without_the_current_version_is_cached_only_briefly()
    {
        await StorePhotoAsync("v2", [9]);
        var client = _api.CreateClient();

        var response = await client.GetAsync($"/api/product-photos/{Coke}?v=v1", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=300", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_browser_that_has_the_picture_gets_not_modified()
    {
        await StorePhotoAsync("v1", [1, 2, 3]);
        var client = _api.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/product-photos/0{Coke}?v=v1");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"v1\""));

        var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }

    [Theory]
    [InlineData("9002490100070")]
    [InlineData("not-a-barcode")]
    public async Task A_code_without_a_photo_is_not_found(string code)
    {
        var response = await _api.CreateClient().GetAsync($"/api/product-photos/{code}", Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private static async Task<HttpResponseMessage> CreateCokeAsync(HttpClient client, string owner)
    {
        var categories = await BodyAsync(await SendAsync(client, HttpMethod.Get, "/api/categories", owner));
        var beverages = categories.EnumerateArray()
            .Single(node => node.GetProperty("name").GetString() == "Bebidas")
            .GetProperty("id")
            .GetGuid();

        return await SendAsync(client, HttpMethod.Post, "/api/products", owner,
            new { name = "Coca-Cola Lata 350ml", categoryId = beverages, barcode = Coke, salePriceCents = 550 });
    }

    /// <summary>What the worker leaves behind once it has found and cut out the photo.</summary>
    private async Task StorePhotoAsync(string version, byte[] content)
    {
        var store = _api.Services.GetRequiredService<IProductPhotoStore>();
        var gtin = Gtin.Parse(Coke);

        await store.RequestAsync([gtin], Token);
        var photo = await store.ClaimNextAsync(DateTimeOffset.UtcNow.AddYears(1), TimeSpan.FromMinutes(5), Token)
            ?? throw new InvalidOperationException("The requested photo should be due.");

        photo.Store(version, "Open Food Facts", $"https://world.openfoodfacts.org/product/{Coke}", "CC BY-SA 3.0", DateTimeOffset.UtcNow);
        await store.SaveAsync(photo, new PhotoFile(version, "image/webp", content), Token);
    }
}
