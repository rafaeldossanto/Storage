using Storage.Application.Stock;
using Storage.Application.Tests.Fakes;
using Storage.Domain.Stock;

namespace Storage.Application.Tests.Stock;

public sealed class ExpiryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 26);
    private static readonly Guid Shop = Guid.CreateVersion7();
    private static readonly Guid Drink = Guid.CreateVersion7();

    private readonly InMemoryStockStore _stock = new(Shop);
    private readonly ExpiryService _service;

    public ExpiryServiceTests() =>
        _service = new ExpiryService(_stock, new FixedCalendar(Today), new ManualClock(Now));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Batches_past_their_date_become_losses_valued_at_cost()
    {
        var yesterday = _stock.Seed(Drink, 10, 500, Today.AddDays(-1), Now.AddDays(-20));
        var lastWeek = _stock.Seed(Drink, 4, 300, Today.AddDays(-7), Now.AddDays(-30));

        var sweep = await _service.ExpireDueAsync(Token);

        Assert.Equal(new ExpirySweepDto(2, 14, 6200), sweep);
        Assert.Equal(BatchStatus.Expired, yesterday.Status);
        Assert.Equal(BatchStatus.Expired, lastWeek.Status);
        Assert.All(_stock.Movements, movement =>
        {
            Assert.Equal(MovementType.ExpiryLoss, movement.Type);
            Assert.Null(movement.UserId);
            Assert.Equal(Now, movement.OccurredAt);
        });
    }

    [Fact]
    public async Task A_batch_expiring_today_is_still_on_sale()
    {
        var today = _stock.Seed(Drink, 10, 500, Today, Now.AddDays(-20));

        var sweep = await _service.ExpireDueAsync(Token);

        Assert.Equal(0, sweep.Batches);
        Assert.Equal(BatchStatus.Available, today.Status);
    }

    [Fact]
    public async Task Only_what_is_left_in_the_batch_is_lost()
    {
        var batch = _stock.Seed(Drink, 10, 500, Today.AddDays(-1), Now.AddDays(-20));
        batch.Take(7);

        var sweep = await _service.ExpireDueAsync(Token);

        Assert.Equal(3, sweep.Units);
        Assert.Equal(-3, Assert.Single(_stock.Movements).Quantity);
    }

    [Fact]
    public async Task Nothing_due_means_nothing_written()
    {
        _stock.Seed(Drink, 10, 500, Today.AddDays(10), Now);
        _stock.Seed(Drink, 10, 500, expiry: null, Now);

        await _service.ExpireDueAsync(Token);

        Assert.Equal(0, _stock.Commits);
    }
}
