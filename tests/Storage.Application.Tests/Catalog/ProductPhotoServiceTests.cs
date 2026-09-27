using Storage.Application.Catalog;
using Storage.Application.Tests.Fakes;
using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;

namespace Storage.Application.Tests.Catalog;

public sealed class ProductPhotoServiceTests
{
    private static readonly Gtin Coke = Gtin.Parse("7894900010015");
    private static readonly Gtin RedBull = Gtin.Parse("9002490100070");

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryProductPhotoStore _store;
    private readonly FakePhotoSource _source = new();
    private readonly ProductPhotoService _service;

    public ProductPhotoServiceTests()
    {
        _store = new InMemoryProductPhotoStore(_clock);
        _service = new ProductPhotoService(_store, _source, new FakeStudio(), _clock);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_photo_found_is_cut_out_stored_and_credited()
    {
        _source.Knows(Coke, [1, 2, 3]);
        await _store.RequestAsync([Coke], Token);

        var attempt = await _service.ProcessNextAsync(Token);

        Assert.Equal(PhotoAttemptOutcome.Stored, attempt!.Outcome);
        var photo = _store.Photos[Coke];
        Assert.Equal(ProductPhotoStatus.Ready, photo.Status);
        Assert.Equal("Open Food Facts", photo.Source);
        Assert.Equal("CC BY-SA 3.0", photo.License);

        // What the browser gets is what the studio made, under the version the photo carries.
        var file = await _service.OpenAsync(Coke.Value, Token);
        Assert.Equal([3, 2, 1], file!.Content);
        Assert.Equal(photo.Version, file.Version);
    }

    [Fact]
    public async Task A_code_no_source_knows_is_left_for_a_month()
    {
        await _store.RequestAsync([Coke], Token);

        var attempt = await _service.ProcessNextAsync(Token);

        Assert.Equal(PhotoAttemptOutcome.Missing, attempt!.Outcome);
        Assert.Equal(ProductPhotoStatus.Missing, _store.Photos[Coke].Status);
        Assert.Null(await _service.ProcessNextAsync(Token));
    }

    [Fact]
    public async Task A_source_that_fails_leaves_the_code_to_be_tried_again()
    {
        _source.FailsFor(Coke);
        await _store.RequestAsync([Coke], Token);

        var attempt = await _service.ProcessNextAsync(Token);

        Assert.Equal(PhotoAttemptOutcome.Failed, attempt!.Outcome);
        Assert.IsType<HttpRequestException>(attempt.Error);
        Assert.Equal(ProductPhotoStatus.Pending, _store.Photos[Coke].Status);

        // Not straight away - a source that is down stays down for a while - but again.
        Assert.Null(await _service.ProcessNextAsync(Token));
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await _service.ProcessNextAsync(Token));
    }

    [Fact]
    public async Task A_photo_that_cannot_be_read_counts_as_none()
    {
        _source.Knows(Coke, [0, 9, 9]);
        await _store.RequestAsync([Coke], Token);

        var attempt = await _service.ProcessNextAsync(Token);

        Assert.Equal(PhotoAttemptOutcome.Missing, attempt!.Outcome);
        Assert.Equal(ProductPhotoStatus.Missing, _store.Photos[Coke].Status);
    }

    [Fact]
    public async Task Codes_are_worked_through_one_at_a_time_until_none_is_due()
    {
        _source.Knows(Coke, [1]);
        _source.Knows(RedBull, [2]);
        await _store.RequestAsync([Coke, RedBull], Token);

        Assert.NotNull(await _service.ProcessNextAsync(Token));
        Assert.NotNull(await _service.ProcessNextAsync(Token));
        Assert.Null(await _service.ProcessNextAsync(Token));

        Assert.Equal(2, _source.Asked.Count);
        Assert.Contains(Coke, _source.Asked);
        Assert.Contains(RedBull, _source.Asked);
    }

    [Fact]
    public async Task Asking_again_for_a_ready_photo_changes_nothing()
    {
        _source.Knows(Coke, [1]);
        await _store.RequestAsync([Coke], Token);
        await _service.ProcessNextAsync(Token);

        await _store.RequestAsync([Coke], Token);

        Assert.Equal(ProductPhotoStatus.Ready, _store.Photos[Coke].Status);
        Assert.Null(await _service.ProcessNextAsync(Token));
    }

    [Fact]
    public async Task An_address_that_is_not_a_barcode_has_no_photo()
    {
        Assert.Null(await _service.OpenAsync("../../etc/passwd", Token));
    }
}
