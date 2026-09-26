using Storage.Application.Abstractions;
using Storage.Application.Errors;
using Storage.Domain.Accounts;

namespace Storage.Application.Accounts;

public sealed class AuthService(
    IAccountStore accounts,
    IPasswordHasher hasher,
    IAccessTokenIssuer accessTokens,
    SessionPolicy policy,
    TimeProvider clock)
{
    /// <summary>
    /// Creates a shop, its owner and a starting category tree, and signs the owner in.
    /// </summary>
    public async Task<AuthResult> SignUpAsync(SignUpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var email = EmailAddress.Parse(request.Email);
        var password = PasswordPolicy.Require(request.Password);

        if (await accounts.EmailInUseAsync(email, cancellationToken))
        {
            throw UseCaseException.Conflict(ErrorCodes.EmailTaken, "This e-mail already has an account.");
        }

        var tenant = Tenant.Create(request.ShopName, request.TimeZone);
        var owner = User.CreateOwner(tenant.Id, request.OwnerName, email, hasher.Hash(password));

        await accounts.ProvisionAsync(tenant, owner, StarterCatalog.For(tenant.Id), cancellationToken);

        return await StartSessionAsync(owner, tenant, cancellationToken);
    }

    public async Task<AuthResult> SignInAsync(SignInRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = clock.GetUtcNow();

        // Oversized input never reaches the hasher; it cannot be anyone's password anyway.
        var password = request.Password is { Length: > 0 and <= PasswordPolicy.MaxLength }
            ? request.Password
            : null;

        var user = password is not null && EmailAddress.TryParse(request.Email, out var email)
            ? await accounts.FindUserByEmailAsync(email, cancellationToken)
            : null;

        if (user is null)
        {
            // Spend the same time a real check would: otherwise how long the answer takes
            // tells an attacker which e-mails have an account.
            hasher.VerifyDecoy(password ?? string.Empty);
            throw InvalidCredentials();
        }

        if (user.IsLockedOut(now))
        {
            throw new UseCaseException(
                ErrorKind.TooManyAttempts,
                ErrorCodes.LockedOut,
                "Too many wrong passwords; try again later.");
        }

        var verification = hasher.Verify(user.PasswordHash, password!);

        if (verification == PasswordVerification.Failed)
        {
            user.RecordFailedSignIn(now);
            await accounts.UpdateUserAsync(user, cancellationToken);
            throw InvalidCredentials();
        }

        // Only now, with the right password in hand, is it safe to say more than "no".
        if (!user.Active)
        {
            throw UseCaseException.Unauthorized(ErrorCodes.AccountInactive, "This account was deactivated.");
        }

        var tenant = await RequireActiveShopAsync(user, cancellationToken);

        if (verification == PasswordVerification.SucceededNeedsRehash)
        {
            // The one moment the plain password is available to upgrade how it is stored.
            user.ChangePasswordHash(hasher.Hash(password!));
        }

        user.RecordSuccessfulSignIn();
        await accounts.UpdateUserAsync(user, cancellationToken);

        return await StartSessionAsync(user, tenant, cancellationToken);
    }

    /// <summary>
    /// Trades a refresh token for a new access token and a new refresh token.
    /// </summary>
    /// <remarks>
    /// Rotation is strict: each refresh token works once. Two tabs refreshing with the same
    /// token at the same instant will sign the person out, because the second use is
    /// indistinguishable from a stolen copy - the front end refreshes from one place only.
    /// </remarks>
    public async Task<AuthResult> RefreshAsync(string? refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw SessionInvalid();
        }

        var now = clock.GetUtcNow();

        var session = await accounts.FindSessionByTokenHashAsync(RefreshTokens.HashOf(refreshToken), cancellationToken)
            ?? throw SessionInvalid();

        if (session.IsRevoked)
        {
            // A retired token came back, so someone kept a copy of it. Nobody can tell the
            // owner from the thief, so every session of that person ends.
            await accounts.RevokeAllSessionsAsync(session.UserId, now, cancellationToken);
            throw SessionInvalid();
        }

        if (!session.IsActive(now))
        {
            throw SessionInvalid();
        }

        var user = await accounts.FindUserAsync(session.UserId, cancellationToken);
        var tenant = user is null ? null : await accounts.FindTenantAsync(user.TenantId, cancellationToken);

        // Deactivated since signing in, or the shop was suspended: the session ends here.
        if (user is not { Active: true } || tenant is not { Active: true })
        {
            session.Revoke(now);
            await accounts.UpdateSessionAsync(session, cancellationToken);
            throw SessionInvalid();
        }

        var (token, hash) = RefreshTokens.Create();
        var next = session.Rotate(hash, now, policy.RefreshTokenLifetime);

        if (!await accounts.RotateSessionAsync(session, next, cancellationToken))
        {
            // Another refresh with this very token got there first: the same reuse as above,
            // only closer together.
            await accounts.RevokeAllSessionsAsync(session.UserId, now, cancellationToken);
            throw SessionInvalid();
        }

        return Result(user, tenant, token, next);
    }

    /// <summary>Ends the session behind a refresh token. Signing out twice is harmless.</summary>
    public async Task SignOutAsync(string? refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var session = await accounts.FindSessionByTokenHashAsync(RefreshTokens.HashOf(refreshToken), cancellationToken);

        if (session is { IsRevoked: false })
        {
            session.Revoke(clock.GetUtcNow());
            await accounts.UpdateSessionAsync(session, cancellationToken);
        }
    }

    /// <summary>The signed-in person and their shop, for the screen's header and menus.</summary>
    public async Task<AccountDto> GetAccountAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var user = await accounts.FindUserAsync(userId, cancellationToken);

        // The token names both; they must still agree with what is stored.
        if (user is null || user.TenantId != tenantId)
        {
            throw UseCaseException.NotFound(ErrorCodes.UserNotFound, "Account not found.");
        }

        var tenant = await RequireActiveShopAsync(user, cancellationToken);
        return ToAccount(user, tenant);
    }

    private async Task<AuthResult> StartSessionAsync(User user, Tenant tenant, CancellationToken cancellationToken)
    {
        var (token, hash) = RefreshTokens.Create();
        var session = Session.Start(user, hash, clock.GetUtcNow(), policy.RefreshTokenLifetime);

        await accounts.AddSessionAsync(session, cancellationToken);

        return Result(user, tenant, token, session);
    }

    private AuthResult Result(User user, Tenant tenant, string refreshToken, Session session) =>
        new(accessTokens.Issue(user), refreshToken, session.ExpiresAt, ToAccount(user, tenant));

    private async Task<Tenant> RequireActiveShopAsync(User user, CancellationToken cancellationToken)
    {
        var tenant = await accounts.FindTenantAsync(user.TenantId, cancellationToken);

        return tenant is { Active: true }
            ? tenant
            : throw UseCaseException.Unauthorized(ErrorCodes.ShopInactive, "This shop is not active.");
    }

    private static AccountDto ToAccount(User user, Tenant tenant) => new(
        user.Id,
        user.Name,
        user.Email.Value,
        user.Role,
        tenant.Id,
        tenant.Name,
        tenant.TimeZoneId);

    private static UseCaseException InvalidCredentials() =>
        UseCaseException.Unauthorized(ErrorCodes.InvalidCredentials, "Wrong e-mail or password.");

    private static UseCaseException SessionInvalid() =>
        UseCaseException.Unauthorized(ErrorCodes.SessionInvalid, "The session is no longer valid; sign in again.");
}
