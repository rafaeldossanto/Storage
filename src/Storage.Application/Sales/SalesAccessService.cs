using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Accounts;
using Storage.Domain.Common;

namespace Storage.Application.Sales;

public sealed record SalesPinRequest(string Pin);

public sealed record SalesPinStatusDto(bool Configured, DateTimeOffset? LockedUntil);

public sealed record SalesAccessDto(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// The PIN that opens the sales area - like a computer's PIN: a few digits, a few tries,
/// then a wait.
/// </summary>
public sealed class SalesAccessService(
    IAccountStore accounts,
    IPasswordHasher hasher,
    ISalesAccessIssuer issuer,
    ITenantContext tenant,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public const int PinMinLength = 4;
    public const int PinMaxLength = 8;

    // Two people typing at the same moment is the most there will ever be.
    private const int SaveAttempts = 3;

    public async Task<SalesPinStatusDto> StatusAsync(CancellationToken cancellationToken = default)
    {
        var shop = await ShopAsync(cancellationToken);
        var now = clock.GetUtcNow();

        return new SalesPinStatusDto(shop.HasSalesPin, shop.IsSalesPinLocked(now) ? shop.SalesPinLockedUntil : null);
    }

    /// <summary>Sets or replaces the PIN. The owner's call; the endpoint makes sure of that.</summary>
    public async Task SetPinAsync(SalesPinRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hash = hasher.Hash(RequireFormat(request.Pin));

        await SaveAsync(shop => shop.SetSalesPin(hash), cancellationToken);
    }

    /// <summary>Trades the right PIN for a pass to the sales area.</summary>
    public async Task<SalesAccessDto> UnlockAsync(SalesPinRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var shop = await ShopAsync(cancellationToken);
        var now = clock.GetUtcNow();

        if (!shop.HasSalesPin)
        {
            throw UseCaseException.Conflict(ErrorCodes.SalesPinNotSet, "The owner has not set the sales PIN yet.");
        }

        // Checked before the PIN itself: while locked, even the right PIN does not open, or
        // the lock would only slow guessing down instead of stopping it.
        if (shop.IsSalesPinLocked(now))
        {
            throw Locked();
        }

        var pin = request.Pin ?? string.Empty;
        var right = pin.Length <= PinMaxLength && hasher.Verify(shop.SalesPinHash!, pin) != PasswordVerification.Failed;

        if (!right)
        {
            var locked = false;
            await SaveAsync(
                current =>
                {
                    current.RecordSalesPinFailure(now);
                    locked = current.IsSalesPinLocked(now);
                },
                cancellationToken);

            throw locked
                ? Locked()
                : UseCaseException.Invalid(ErrorCodes.SalesPinWrong, "That is not the sales PIN.");
        }

        if (shop.RecordSalesPinSuccess())
        {
            await SaveAsync(current => current.RecordSalesPinSuccess(), cancellationToken);
        }

        var access = issuer.Issue(tenant.TenantId, currentUser.UserId);
        return new SalesAccessDto(access.Token, access.ExpiresAt);
    }

    private static string RequireFormat(string? pin) =>
        pin is { Length: >= PinMinLength and <= PinMaxLength } && pin.All(char.IsAsciiDigit)
            ? pin
            : throw new DomainException(DomainErrors.SalesPinFormat, $"A PIN has {PinMinLength} to {PinMaxLength} digits.");

    // Reads the shop, applies the change, and writes it only if nobody changed the PIN's
    // state in between; otherwise reads again and reapplies.
    private async Task SaveAsync(Action<Tenant> change, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < SaveAttempts; attempt++)
        {
            var shop = await ShopAsync(cancellationToken);
            var version = shop.SalesPinVersion;
            change(shop);

            if (await accounts.SaveSalesPinAsync(shop, version, cancellationToken))
            {
                return;
            }
        }

        throw UseCaseException.Conflict(ErrorCodes.SalesPinBusy, "The PIN changed meanwhile; try again.");
    }

    private async Task<Tenant> ShopAsync(CancellationToken cancellationToken) =>
        await accounts.FindTenantAsync(tenant.TenantId, cancellationToken)
        ?? throw new InvalidOperationException($"Tenant {tenant.TenantId} does not exist.");

    private static UseCaseException Locked() =>
        new(ErrorKind.TooManyAttempts, ErrorCodes.SalesPinLocked, "Too many wrong PINs; the sales area is locked for a while.");
}
