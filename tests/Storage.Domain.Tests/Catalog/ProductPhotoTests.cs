using Storage.Domain.Catalog;
using Storage.Domain.ValueObjects;

namespace Storage.Domain.Tests.Catalog;

public class ProductPhotoTests
{
    private static readonly Gtin Can = Gtin.Parse("7894900010015");
    private static readonly DateTimeOffset Noon = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_request_is_due_at_once()
    {
        var photo = ProductPhoto.Request(Can, Noon);

        Assert.Equal(ProductPhotoStatus.Pending, photo.Status);
        Assert.True(photo.IsDue(Noon));
    }

    [Fact]
    public void A_code_the_shop_minted_has_no_photo_to_look_up()
    {
        // 2 as the first digit of the EAN-13: a scale label, a code of one shop only.
        var weighed = Gtin.Parse("200000100000" + Gtin.CalculateCheckDigit("200000100000"));

        Assert.Throws<ArgumentException>(() => ProductPhoto.Request(weighed, Noon));
    }

    [Fact]
    public void A_stored_photo_is_never_due_again()
    {
        var photo = ProductPhoto.Request(Can, Noon);

        photo.Store("abc123", "Open Food Facts", "https://world.openfoodfacts.org/product/7894900010015", "CC BY-SA 3.0", Noon);

        Assert.Equal(ProductPhotoStatus.Ready, photo.Status);
        Assert.Equal("abc123", photo.Version);
        Assert.False(photo.IsDue(Noon.AddYears(5)));
    }

    [Fact]
    public void A_code_nobody_knows_is_looked_up_again_a_month_later()
    {
        var photo = ProductPhoto.Request(Can, Noon);

        photo.MarkMissing(Noon);

        Assert.Equal(ProductPhotoStatus.Missing, photo.Status);
        Assert.False(photo.IsDue(Noon.AddDays(29)));
        Assert.True(photo.IsDue(Noon + ProductPhoto.RecheckMissingAfter));
    }

    [Fact]
    public void Failures_in_a_row_wait_longer_each_time_up_to_a_day()
    {
        var photo = ProductPhoto.Request(Can, Noon);
        var waits = new List<TimeSpan>();

        for (var attempt = 0; attempt < 7; attempt++)
        {
            photo.RecordFailure(Noon);
            waits.Add(photo.NextAttemptAt - Noon);
        }

        Assert.Equal(
            [
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6),
                TimeSpan.FromDays(1), TimeSpan.FromDays(1), TimeSpan.FromDays(1),
            ],
            waits);
        Assert.Equal(ProductPhotoStatus.Pending, photo.Status);
    }

    [Fact]
    public void A_clean_answer_starts_the_failure_count_over()
    {
        var photo = ProductPhoto.Request(Can, Noon);
        photo.RecordFailure(Noon);
        photo.RecordFailure(Noon);

        photo.MarkMissing(Noon);

        Assert.Equal(0, photo.FailedAttempts);
    }
}
