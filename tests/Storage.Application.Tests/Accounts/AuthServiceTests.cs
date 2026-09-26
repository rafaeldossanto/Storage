using Storage.Application.Accounts;
using Storage.Application.Errors;
using Storage.Application.Tests.Fakes;
using Storage.Domain.Accounts;

namespace Storage.Application.Tests.Accounts;

public sealed class AuthServiceTests
{
    private const string Email = "ze@mercadinho.com";
    private const string Password = "cafe-com-pao-2026";

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAccountStore _store = new();
    private readonly FakePasswordHasher _hasher = new();
    private readonly AuthService _service;

    public AuthServiceTests() =>
        _service = new AuthService(
            _store,
            _hasher,
            new FakeAccessTokenIssuer(_clock),
            new SessionPolicy(TimeSpan.FromDays(30)),
            _clock);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- sign-up ---------------------------------------------------------------------

    [Fact]
    public async Task Signing_up_creates_the_shop_with_its_owner_signed_in()
    {
        var result = await SignUpAsync();

        Assert.Equal(UserRole.Owner, result.Account.Role);
        Assert.Equal("Mercadinho do Zé", result.Account.ShopName);
        Assert.Equal("America/Sao_Paulo", result.Account.ShopTimeZone);
        Assert.Single(_store.Sessions);
    }

    [Fact]
    public async Task A_new_shop_starts_with_a_category_tree_of_its_own()
    {
        var result = await SignUpAsync();

        var beverages = Assert.Single(_store.Categories, category => category.Name == "Bebidas");
        var energy = Assert.Single(_store.Categories, category => category.Name == "Energéticos");

        Assert.True(energy.IsDescendantOf(beverages));
        Assert.All(_store.Categories, category => Assert.Equal(result.Account.ShopId, category.TenantId));
    }

    [Fact]
    public async Task An_e_mail_that_already_has_an_account_is_a_conflict()
    {
        await SignUpAsync();

        await Refused.WithAsync(ErrorKind.Conflict, ErrorCodes.EmailTaken, () => SignUpAsync(email: "ZE@mercadinho.com"));
    }

    [Fact]
    public async Task A_password_shorter_than_eight_characters_is_refused()
    {
        await Refused.WithAsync(ErrorKind.Invalid, ErrorCodes.PasswordInvalid, () => SignUpAsync(password: "1234567"));
    }

    [Fact]
    public async Task The_password_is_never_stored_as_typed()
    {
        await SignUpAsync();

        Assert.NotEqual(Password, Assert.Single(_store.Users).PasswordHash);
    }

    // ---- sign-in ---------------------------------------------------------------------

    [Fact]
    public async Task The_right_password_signs_in_whatever_the_e_mails_casing()
    {
        await SignUpAsync();

        var result = await SignInAsync(email: "Ze@Mercadinho.COM");

        Assert.Equal(Email, result.Account.Email);
    }

    [Fact]
    public async Task Only_a_hash_of_the_refresh_token_is_kept()
    {
        await SignUpAsync();

        var result = await SignInAsync();

        // Whoever reads the database cannot sign in with what they find there.
        Assert.DoesNotContain(_store.Sessions, session => session.TokenHash == result.RefreshToken);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_e_mail_get_the_same_answer()
    {
        await SignUpAsync();

        await Refused.WithAsync(
            ErrorKind.Unauthorized, ErrorCodes.InvalidCredentials, () => SignInAsync(password: "senha-errada"));

        await Refused.WithAsync(
            ErrorKind.Unauthorized, ErrorCodes.InvalidCredentials, () => SignInAsync(email: "ninguem@loja.com"));
    }

    [Fact]
    public async Task An_unknown_e_mail_still_costs_the_time_of_a_real_check()
    {
        await Refused.WithAsync(
            ErrorKind.Unauthorized, ErrorCodes.InvalidCredentials, () => SignInAsync(email: "ninguem@loja.com"));

        // Answering faster when nobody has that e-mail would leak which e-mails exist.
        Assert.Equal(1, _hasher.DecoyChecks);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_against_the_right_one()
    {
        await SignUpAsync();

        for (var attempt = 0; attempt < User.MaxFailedSignIns; attempt++)
        {
            await Refused.WithAsync(
                ErrorKind.Unauthorized, ErrorCodes.InvalidCredentials, () => SignInAsync(password: "senha-errada"));
        }

        await Refused.WithAsync(ErrorKind.TooManyAttempts, ErrorCodes.LockedOut, () => SignInAsync());
    }

    [Fact]
    public async Task The_lock_lifts_after_fifteen_minutes()
    {
        await SignUpAsync();

        for (var attempt = 0; attempt < User.MaxFailedSignIns; attempt++)
        {
            await Refused.WithAsync(
                ErrorKind.Unauthorized, ErrorCodes.InvalidCredentials, () => SignInAsync(password: "senha-errada"));
        }

        _clock.Advance(User.LockoutDuration);

        Assert.Equal(Email, (await SignInAsync()).Account.Email);
    }

    [Fact]
    public async Task A_deactivated_person_is_told_so_only_after_the_right_password()
    {
        await SignUpAsync();
        var staff = AddStaff("ana@mercadinho.com");
        staff.Deactivate();

        // Without the password: the same answer as anyone else.
        await Refused.WithAsync(
            ErrorKind.Unauthorized,
            ErrorCodes.InvalidCredentials,
            () => SignInAsync(email: "ana@mercadinho.com", password: "senha-errada"));

        await Refused.WithAsync(
            ErrorKind.Unauthorized, ErrorCodes.AccountInactive, () => SignInAsync(email: "ana@mercadinho.com"));
    }

    [Fact]
    public async Task A_suspended_shop_cannot_sign_in()
    {
        await SignUpAsync();
        Assert.Single(_store.Tenants).Suspend();

        await Refused.WithAsync(ErrorKind.Unauthorized, ErrorCodes.ShopInactive, () => SignInAsync());
    }

    [Fact]
    public async Task An_outdated_hash_is_upgraded_at_sign_in()
    {
        await SignUpAsync();
        var owner = Assert.Single(_store.Users);
        owner.ChangePasswordHash($"old:{Password}");

        await SignInAsync();

        Assert.Equal($"hash:{Password}", owner.PasswordHash);
    }

    // ---- refresh and sign-out ------------------------------------------------------

    [Fact]
    public async Task Refreshing_hands_out_a_new_refresh_token()
    {
        var signedIn = await SignUpAsync();

        var refreshed = await _service.RefreshAsync(signedIn.RefreshToken, Token);

        Assert.NotEqual(signedIn.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(signedIn.Account.UserId, refreshed.Account.UserId);
    }

    [Fact]
    public async Task A_refresh_token_that_comes_back_after_use_ends_every_session()
    {
        var signedIn = await SignUpAsync();
        var refreshed = await _service.RefreshAsync(signedIn.RefreshToken, Token);

        // The first token was used already; seeing it again means someone kept a copy.
        await Refused.WithAsync(
            ErrorKind.Unauthorized, ErrorCodes.SessionInvalid, () => _service.RefreshAsync(signedIn.RefreshToken, Token));

        // Nobody can tell owner from thief, so the legitimate new token dies too.
        await Refused.WithAsync(
            ErrorKind.Unauthorized, ErrorCodes.SessionInvalid, () => _service.RefreshAsync(refreshed.RefreshToken, Token));
    }

    [Fact]
    public async Task A_refresh_that_loses_the_race_to_the_same_token_ends_every_session()
    {
        var signedIn = await SignUpAsync();
        _store.LoseNextRotation = true;

        await Refused.WithAsync(
            ErrorKind.Unauthorized, ErrorCodes.SessionInvalid, () => _service.RefreshAsync(signedIn.RefreshToken, Token));

        Assert.All(_store.Sessions, session => Assert.True(session.IsRevoked));
    }

    [Fact]
    public async Task A_session_idle_for_thirty_days_is_over()
    {
        var signedIn = await SignUpAsync();

        _clock.Advance(TimeSpan.FromDays(30));

        await Refused.WithAsync(
            ErrorKind.Unauthorized, ErrorCodes.SessionInvalid, () => _service.RefreshAsync(signedIn.RefreshToken, Token));
    }

    [Fact]
    public async Task A_made_up_refresh_token_is_refused()
    {
        await Refused.WithAsync(
            ErrorKind.Unauthorized, ErrorCodes.SessionInvalid, () => _service.RefreshAsync("inventado", Token));
    }

    [Fact]
    public async Task Someone_deactivated_since_signing_in_cannot_refresh()
    {
        await SignUpAsync();
        var staff = AddStaff("ana@mercadinho.com");
        var signedIn = await SignInAsync(email: "ana@mercadinho.com");

        staff.Deactivate();

        await Refused.WithAsync(
            ErrorKind.Unauthorized, ErrorCodes.SessionInvalid, () => _service.RefreshAsync(signedIn.RefreshToken, Token));
    }

    [Fact]
    public async Task Signing_out_ends_the_session_and_can_be_repeated()
    {
        var signedIn = await SignUpAsync();

        await _service.SignOutAsync(signedIn.RefreshToken, Token);
        await _service.SignOutAsync(signedIn.RefreshToken, Token);

        Assert.True(Assert.Single(_store.Sessions).IsRevoked);
    }

    [Fact]
    public async Task The_account_behind_a_token_must_still_belong_to_the_same_shop()
    {
        var signedIn = await SignUpAsync();

        await Refused.WithAsync(
            ErrorKind.NotFound,
            ErrorCodes.UserNotFound,
            () => _service.GetAccountAsync(signedIn.Account.UserId, Guid.CreateVersion7(), Token));
    }

    // ---- helpers -----------------------------------------------------------------------

    private Task<AuthResult> SignUpAsync(string email = Email, string password = Password) =>
        _service.SignUpAsync(new SignUpRequest("Mercadinho do Zé", "Zé", email, password), Token);

    private Task<AuthResult> SignInAsync(string email = Email, string password = Password) =>
        _service.SignInAsync(new SignInRequest(email, password), Token);

    private User AddStaff(string email)
    {
        var shop = Assert.Single(_store.Tenants);
        var staff = User.CreateStaff(shop.Id, "Ana", EmailAddress.Parse(email), $"hash:{Password}");
        _store.AddUser(staff);
        return staff;
    }
}
