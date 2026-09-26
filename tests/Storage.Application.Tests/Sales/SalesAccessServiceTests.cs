using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Application.Sales;
using Storage.Application.Tests.Fakes;
using Storage.Domain.Accounts;
using Storage.Domain.Common;

namespace Storage.Application.Tests.Sales;

public sealed class SalesAccessServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualClock _clock = new(Now);
    private readonly InMemoryAccountStore _accounts = new();
    private readonly Tenant _shop = Tenant.Create("Mercadinho");
    private readonly Guid _user = Guid.CreateVersion7();
    private readonly SalesAccessService _service;

    public SalesAccessServiceTests()
    {
        _accounts.Tenants.Add(_shop);
        _service = new SalesAccessService(
            _accounts, new FakePasswordHasher(), new FakeSalesAccessIssuer(_clock), new FixedTenant(_shop.Id), new FixedUser(_user), _clock);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_a_pin_the_sales_area_cannot_be_opened()
    {
        Assert.False((await _service.StatusAsync(Token)).Configured);

        await Refused.WithAsync(
            ErrorKind.Conflict, ErrorCodes.SalesPinNotSet, () => _service.UnlockAsync(new SalesPinRequest("1234"), Token));
    }

    [Fact]
    public async Task The_right_pin_hands_out_a_pass_for_this_person_in_this_shop()
    {
        await _service.SetPinAsync(new SalesPinRequest("2580"), Token);

        var access = await _service.UnlockAsync(new SalesPinRequest("2580"), Token);

        Assert.Equal($"sales:{_shop.Id}:{_user}", access.Token);
        Assert.True((await _service.StatusAsync(Token)).Configured);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("123456789")]
    [InlineData("12a4")]
    [InlineData("")]
    public async Task A_pin_is_four_to_eight_digits(string pin)
    {
        var refusal = await Assert.ThrowsAsync<DomainException>(() => _service.SetPinAsync(new SalesPinRequest(pin), Token));

        Assert.Equal(DomainErrors.SalesPinFormat, refusal.Code);
    }

    [Fact]
    public async Task A_wrong_pin_is_refused_and_counted()
    {
        await _service.SetPinAsync(new SalesPinRequest("2580"), Token);

        await Refused.WithAsync(
            ErrorKind.Invalid, ErrorCodes.SalesPinWrong, () => _service.UnlockAsync(new SalesPinRequest("0000"), Token));

        Assert.Equal(1, _shop.SalesPinFailures);
    }

    [Fact]
    public async Task The_fifth_wrong_pin_locks_and_then_even_the_right_one_waits()
    {
        await _service.SetPinAsync(new SalesPinRequest("2580"), Token);

        for (var attempt = 1; attempt < Tenant.SalesPinMaxFailures; attempt++)
        {
            await Refused.WithAsync(
                ErrorKind.Invalid, ErrorCodes.SalesPinWrong, () => _service.UnlockAsync(new SalesPinRequest("0000"), Token));
        }

        await Refused.WithAsync(
            ErrorKind.TooManyAttempts, ErrorCodes.SalesPinLocked, () => _service.UnlockAsync(new SalesPinRequest("0000"), Token));
        await Refused.WithAsync(
            ErrorKind.TooManyAttempts, ErrorCodes.SalesPinLocked, () => _service.UnlockAsync(new SalesPinRequest("2580"), Token));

        Assert.NotNull((await _service.StatusAsync(Token)).LockedUntil);

        _clock.Advance(Tenant.SalesPinLockout + TimeSpan.FromSeconds(1));
        await _service.UnlockAsync(new SalesPinRequest("2580"), Token);
    }

    [Fact]
    public async Task A_right_pin_clears_the_wrong_ones_before_it()
    {
        await _service.SetPinAsync(new SalesPinRequest("2580"), Token);
        await Refused.WithAsync(
            ErrorKind.Invalid, ErrorCodes.SalesPinWrong, () => _service.UnlockAsync(new SalesPinRequest("0000"), Token));

        await _service.UnlockAsync(new SalesPinRequest("2580"), Token);

        Assert.Equal(0, _shop.SalesPinFailures);
    }

    private sealed class FakeSalesAccessIssuer(TimeProvider clock) : ISalesAccessIssuer
    {
        public SalesAccess Issue(Guid tenantId, Guid userId) =>
            new($"sales:{tenantId}:{userId}", clock.GetUtcNow().AddMinutes(15));
    }
}
