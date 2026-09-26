using System.Net;
using Storage.Integration.Tests.Mongo;
using static Storage.Integration.Tests.Api.ApiCalls;

namespace Storage.Integration.Tests.Api;

/// <summary>
/// The sales area over HTTP: anyone rings up a sale, but what sales were worth needs the PIN.
/// </summary>
public sealed class SalesAccessTests(MongoFixture mongo) : IAsyncDisposable
{
    private const string PassHeader = "X-Sales-Access";

    private readonly StorageApiFactory _api = new(mongo);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_report_is_locked_without_a_pass()
    {
        var client = _api.CreateClient();
        var owner = await SignUpAsync(client, NewEmail());

        var response = await SendAsync(client, HttpMethod.Get, "/api/sales/report?period=Month&date=2026-09-26", owner);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("sales.locked", await CodeAsync(response));
    }

    [Fact]
    public async Task A_sale_rung_up_by_staff_shows_in_the_report_the_pin_opens()
    {
        var client = _api.CreateClient();
        var owner = await SignUpAsync(client, NewEmail());
        var staffEmail = NewEmail();
        (await SendAsync(client, HttpMethod.Post, "/api/team", owner, new { name = "Caixa", email = staffEmail, password = Password }))
            .EnsureSuccessStatusCode();
        var staff = await SignInAsync(client, staffEmail);

        var categories = await BodyAsync(await SendAsync(client, HttpMethod.Get, "/api/categories", owner));
        var beverages = categories.EnumerateArray().Single(node => node.GetProperty("name").GetString() == "Bebidas").GetProperty("id").GetGuid();
        var product = await BodyAsync(await SendAsync(client, HttpMethod.Post, "/api/products", owner,
            new { name = "Energético 473ml", categoryId = beverages, barcode = "7891000000014", salePriceCents = 899, tracksExpiry = false }));
        (await SendAsync(client, HttpMethod.Post, "/api/receipts", owner,
            new { lines = new[] { new { barcode = "7891000000014", quantity = 10, costCents = 500 } } })).EnsureSuccessStatusCode();

        var sold = await SendAsync(client, HttpMethod.Post, "/api/sales", staff,
            new { items = new[] { new { productId = product.GetProperty("id").GetGuid(), quantity = 3 } } });
        Assert.Equal(HttpStatusCode.Created, sold.StatusCode);
        // The till sees the total, not what it cost.
        var sale = await BodyAsync(sold);
        Assert.Equal(2697, sale.GetProperty("totalCents").GetInt64());
        Assert.False(sale.GetProperty("lines")[0].TryGetProperty("costCents", out _));

        (await SendAsync(client, HttpMethod.Put, "/api/sales/pin", owner, new { pin = "2580" })).EnsureSuccessStatusCode();
        var unlocked = await SendAsync(client, HttpMethod.Post, "/api/sales/unlock", staff, new { pin = "2580" });
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        var pass = (await BodyAsync(unlocked)).GetProperty("token").GetString()!;

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/sales/report?period=Day&date={DateTime.UtcNow.AddHours(-3):yyyy-MM-dd}");
        request.Headers.Authorization = new("Bearer", staff);
        request.Headers.Add(PassHeader, pass);
        var report = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        var totals = (await BodyAsync(report)).GetProperty("totals");
        Assert.Equal(2697, totals.GetProperty("revenueCents").GetInt64());
        Assert.Equal(1500, totals.GetProperty("costCents").GetInt64());
        Assert.Equal(1197, totals.GetProperty("netCents").GetInt64());
    }

    [Fact]
    public async Task A_pass_opens_the_report_only_for_whom_it_was_issued()
    {
        var client = _api.CreateClient();
        var owner = await SignUpAsync(client, NewEmail());
        var staffEmail = NewEmail();
        (await SendAsync(client, HttpMethod.Post, "/api/team", owner, new { name = "Caixa", email = staffEmail, password = Password }))
            .EnsureSuccessStatusCode();
        var staff = await SignInAsync(client, staffEmail);
        (await SendAsync(client, HttpMethod.Put, "/api/sales/pin", owner, new { pin = "2580" })).EnsureSuccessStatusCode();
        var staffPass = (await BodyAsync(await SendAsync(client, HttpMethod.Post, "/api/sales/unlock", staff, new { pin = "2580" })))
            .GetProperty("token").GetString()!;

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/sales/report?period=Month&date=2026-09-26");
        request.Headers.Authorization = new("Bearer", owner);
        request.Headers.Add(PassHeader, staffPass);
        var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_pin_is_refused_with_a_code()
    {
        var client = _api.CreateClient();
        var owner = await SignUpAsync(client, NewEmail());
        (await SendAsync(client, HttpMethod.Put, "/api/sales/pin", owner, new { pin = "2580" })).EnsureSuccessStatusCode();

        var response = await SendAsync(client, HttpMethod.Post, "/api/sales/unlock", owner, new { pin = "0000" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("sales.pin_wrong", await CodeAsync(response));
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();
}
