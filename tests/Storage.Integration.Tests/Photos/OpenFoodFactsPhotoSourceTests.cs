using System.Net;
using System.Text;
using Storage.Domain.ValueObjects;
using Storage.Infrastructure.Photos;

namespace Storage.Integration.Tests.Photos;

/// <summary>
/// The Open Food Facts lookup against a fake server answering the way the real API does -
/// no test here reaches the internet.
/// </summary>
public sealed class OpenFoodFactsPhotoSourceTests
{
    private const string Food = "https://world.openfoodfacts.org/";
    private const string Beauty = "https://world.openbeautyfacts.org/";
    private const string FrontPhoto = "https://images.openfoodfacts.org/images/products/789/490/001/0015/front_pt.54.400.jpg";
    private const string FullPhoto = "https://images.openfoodfacts.org/images/products/789/490/001/0015/front_pt.54.full.jpg";

    private static readonly Gtin Coke = Gtin.Parse("7894900010015");

    private readonly FakeServer _server = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_front_photo_comes_in_full_resolution_with_its_credit()
    {
        _server.Json($"{Food}api/v2/product/7894900010015?fields=code,image_front_url", Product(FrontPhoto));
        _server.Bytes(FullPhoto, [1, 2, 3]);

        var photo = await Source().FindAsync(Coke, Token);

        Assert.Equal([1, 2, 3], photo!.Content);
        Assert.Equal("Open Food Facts", photo.Source);
        Assert.Equal("https://world.openfoodfacts.org/product/7894900010015", photo.SourcePage);
        Assert.Equal("CC BY-SA 3.0", photo.License);
    }

    [Fact]
    public async Task Without_a_full_resolution_file_the_400_pixel_one_does()
    {
        _server.Json($"{Food}api/v2/product/7894900010015?fields=code,image_front_url", Product(FrontPhoto));
        _server.Bytes(FrontPhoto, [4, 5]);

        var photo = await Source().FindAsync(Coke, Token);

        Assert.Equal([4, 5], photo!.Content);
    }

    [Fact]
    public async Task A_code_the_food_database_does_not_know_is_asked_of_the_next()
    {
        _server.Json($"{Beauty}api/v2/product/7894900010015?fields=code,image_front_url", Product(
            "https://images.openbeautyfacts.org/images/products/789/490/001/0015/front_pt.3.400.jpg"));
        _server.Bytes("https://images.openbeautyfacts.org/images/products/789/490/001/0015/front_pt.3.full.jpg", [7]);

        var photo = await Source().FindAsync(Coke, Token);

        Assert.Equal("Open Beauty Facts", photo!.Source);
    }

    [Fact]
    public async Task A_code_nobody_knows_has_no_photo()
    {
        Assert.Null(await Source().FindAsync(Coke, Token));
    }

    [Fact]
    public async Task A_product_without_a_front_photo_has_none()
    {
        _server.Json($"{Food}api/v2/product/7894900010015?fields=code,image_front_url", """{"status":1,"product":{"code":"7894900010015"}}""");

        Assert.Null(await Source().FindAsync(Coke, Token));
    }

    [Fact]
    public async Task A_photo_address_outside_the_databases_is_never_fetched()
    {
        _server.Json($"{Food}api/v2/product/7894900010015?fields=code,image_front_url", Product("https://evil.example/steal.jpg"));
        _server.Bytes("https://evil.example/steal.full.jpg", [6]);

        Assert.Null(await Source().FindAsync(Coke, Token));
        Assert.DoesNotContain(_server.Requested, address => address.Contains("evil", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_database_that_fails_is_an_error_to_retry_not_a_missing_photo()
    {
        _server.Fail($"{Food}api/v2/product/7894900010015?fields=code,image_front_url");

        await Assert.ThrowsAsync<HttpRequestException>(() => Source().FindAsync(Coke, Token));
    }

    [Fact]
    public async Task An_eight_digit_code_is_asked_for_as_printed()
    {
        var ean8 = Gtin.Parse("96385074");

        await Source().FindAsync(ean8, Token);

        Assert.Contains($"{Food}api/v2/product/96385074?fields=code,image_front_url", _server.Requested);
    }

    private OpenFoodFactsPhotoSource Source() => new(new HttpClient(_server), new ProductPhotoOptions
    {
        Sites = [new PhotoSite("Open Food Facts", new Uri(Food)), new PhotoSite("Open Beauty Facts", new Uri(Beauty))],
    });

    private static string Product(string frontPhoto) =>
        $$$"""{"code":"7894900010015","status":1,"product":{"image_front_url":"{{{frontPhoto}}}"}}""";

    /// <summary>Answers the addresses it was given; 404 with the API's "not found" body to anything else.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];

        public List<string> Requested { get; } = [];

        public void Json(string address, string body) =>
            _routes[address] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

        public void Bytes(string address, byte[] body) =>
            _routes[address] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

        public void Fail(string address) =>
            _routes[address] = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var address = request.RequestUri!.AbsoluteUri;
            Requested.Add(address);

            return Task.FromResult(_routes.TryGetValue(address, out var answer)
                ? answer()
                : new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"status":0,"status_verbose":"product not found"}"""),
                });
        }
    }
}
