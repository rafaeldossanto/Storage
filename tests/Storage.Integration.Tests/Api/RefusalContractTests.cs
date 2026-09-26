using System.Net;
using System.Text;
using Storage.Integration.Tests.Mongo;
using static Storage.Integration.Tests.Api.ApiCalls;

namespace Storage.Integration.Tests.Api;

/// <summary>
/// What a refusal looks like on the wire. The front end shows a Portuguese message keyed on
/// <c>code</c>, so every refusal - from the domain, the auth pipeline, the rate limiter or
/// the JSON reader - must carry one.
/// </summary>
public sealed class RefusalContractTests(MongoFixture mongo) : IAsyncDisposable
{
    private readonly StorageApiFactory _api = new(mongo);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_token_is_refused_with_the_code_that_tells_the_front_end_to_refresh()
    {
        var response = await SendAsync(_api.CreateClient(), HttpMethod.Get, "/api/products?search=coca");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.unauthenticated", await CodeAsync(response));
    }

    [Fact]
    public async Task Staff_on_an_owners_route_is_refused_with_a_code()
    {
        var client = _api.CreateClient();
        var owner = await SignUpAsync(client, NewEmail());
        var staffEmail = NewEmail();
        (await SendAsync(client, HttpMethod.Post, "/api/team", owner, new { name = "Repositor", email = staffEmail, password = Password }))
            .EnsureSuccessStatusCode();
        var staff = await SignInAsync(client, staffEmail);

        var response = await SendAsync(client, HttpMethod.Post, $"/api/counts/{Guid.CreateVersion7()}/close", staff, new { justification = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", await CodeAsync(response));
    }

    [Fact]
    public async Task A_domain_rule_is_refused_with_its_own_code()
    {
        var client = _api.CreateClient();
        var owner = await SignUpAsync(client, NewEmail());

        var response = await SendAsync(client, HttpMethod.Post, "/api/categories", owner, new { name = "" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("category.name_invalid", await CodeAsync(response));
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_refused_with_a_code()
    {
        using var broken = new StringContent("{\"email\": ", Encoding.UTF8, "application/json");

        var response = await _api.CreateClient().PostAsync("/api/auth/sign-in", broken, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request.malformed", await CodeAsync(response));
    }

    [Fact]
    public async Task An_amount_sent_as_text_is_refused_rather_than_guessed()
    {
        var client = _api.CreateClient();
        var owner = await SignUpAsync(client, NewEmail());

        var response = await SendAsync(client, HttpMethod.Post, "/api/products", owner,
            new { name = "Energético", categoryId = Guid.CreateVersion7(), barcode = "7891000000014", salePriceCents = "899" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request.malformed", await CodeAsync(response));
    }

    [Fact]
    public async Task A_page_out_of_range_is_refused_with_a_code()
    {
        var client = _api.CreateClient();
        var owner = await SignUpAsync(client, NewEmail());

        var response = await SendAsync(client, HttpMethod.Get, "/api/receipts?page=1&pageSize=500", owner);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("page.invalid", await CodeAsync(response));
    }

    [Fact]
    public async Task A_list_comes_as_a_page_that_says_how_much_there_is_in_all()
    {
        var client = _api.CreateClient();
        var owner = await SignUpAsync(client, NewEmail());

        // A new shop comes with its category tree: "Bebidas" and its branch.
        var tree = await BodyAsync(await SendAsync(client, HttpMethod.Get, "/api/categories", owner));
        var beverages = tree.EnumerateArray().Single(node => node.GetProperty("name").GetString() == "Bebidas");
        var categoryId = beverages.GetProperty("id").GetGuid();
        string[] barcodes = ["7891000000014", "7891000000021", "7891000000038"];
        foreach (var barcode in barcodes)
        {
            (await SendAsync(client, HttpMethod.Post, "/api/products", owner,
                new { name = $"Produto {barcode}", categoryId, barcode, salePriceCents = 500 })).EnsureSuccessStatusCode();
        }

        var page = await BodyAsync(await SendAsync(client, HttpMethod.Get, $"/api/products?categoryId={categoryId}&page=2&pageSize=2", owner));

        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.Equal(2, page.GetProperty("page").GetInt32());
        Assert.Equal(2, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, page.GetProperty("total").GetInt64());
        Assert.Equal(2, page.GetProperty("totalPages").GetInt32());
    }

    [Fact]
    public async Task Too_many_sign_in_attempts_get_a_code_and_a_time_to_wait()
    {
        await using var strict = new StorageApiFactory(mongo, authPermitsPerMinute: 2);
        var client = strict.CreateClient();
        var attempt = new { email = NewEmail(), password = "errada-123456" };

        await SendAsync(client, HttpMethod.Post, "/api/auth/sign-in", body: attempt);
        await SendAsync(client, HttpMethod.Post, "/api/auth/sign-in", body: attempt);
        var third = await SendAsync(client, HttpMethod.Post, "/api/auth/sign-in", body: attempt);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("auth.too_many_requests", await CodeAsync(third));
        Assert.True(third.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();
}
