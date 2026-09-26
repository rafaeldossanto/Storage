using Storage.Domain.Accounts;
using Storage.Domain.Common;

namespace Storage.Domain.Tests.Accounts;

public class EmailAddressTests
{
    [Fact]
    public void An_address_is_trimmed_and_lower_cased()
    {
        // Otherwise "Rafael@Loja.com" and "rafael@loja.com" would be two accounts.
        Assert.Equal("rafael@loja.com.br", EmailAddress.Parse("  Rafael@Loja.com.BR ").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("sem-arroba.com")]
    [InlineData("@loja.com")]
    [InlineData("dono@")]
    [InlineData("dono@loja")]
    [InlineData("dono@@loja.com")]
    [InlineData("dono @loja.com")]
    [InlineData("dono@loja.com.")]
    public void Anything_that_cannot_receive_mail_is_refused(string? input)
    {
        DomainAssert.Breaks(DomainErrors.EmailInvalid, () => EmailAddress.Parse(input));
    }
}

public class TenantTests
{
    [Fact]
    public void A_new_shop_runs_on_Sao_Paulo_time_unless_told_otherwise()
    {
        Assert.Equal("America/Sao_Paulo", Tenant.Create("Mercadinho do Zé").TimeZoneId);
    }

    [Fact]
    public void An_unknown_time_zone_is_refused()
    {
        DomainAssert.Breaks(DomainErrors.ShopTimeZoneInvalid, () => Tenant.Create("Mercadinho", "Brasil/Centro"));
    }

    [Fact]
    public void The_shops_date_is_its_own_calendar_not_UTC()
    {
        var shop = Tenant.Create("Mercadinho");

        // 01:30 UTC on the 18th is still 22:30 on the 17th in São Paulo: a batch expiring on
        // the 18th can still be sold. Getting this wrong writes off stock three hours early.
        var instant = new DateTimeOffset(2026, 9, 18, 1, 30, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 9, 17), shop.LocalDate(instant));
    }

    [Fact]
    public void A_shop_needs_a_name()
    {
        DomainAssert.Breaks(DomainErrors.ShopNameInvalid, () => Tenant.Create("  "));
    }
}

public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

    private static User Staff() =>
        User.CreateStaff(Guid.CreateVersion7(), "Ana", EmailAddress.Parse("ana@loja.com"), "hash");

    [Fact]
    public void Four_wrong_passwords_do_not_lock_the_account()
    {
        var user = Staff();

        for (var attempt = 0; attempt < User.MaxFailedSignIns - 1; attempt++)
        {
            user.RecordFailedSignIn(Now);
        }

        Assert.False(user.IsLockedOut(Now));
    }

    [Fact]
    public void The_fifth_wrong_password_locks_the_account_for_fifteen_minutes()
    {
        var user = Staff();

        for (var attempt = 0; attempt < User.MaxFailedSignIns; attempt++)
        {
            user.RecordFailedSignIn(Now);
        }

        Assert.True(user.IsLockedOut(Now));
        Assert.True(user.IsLockedOut(Now.AddMinutes(14)));
        Assert.False(user.IsLockedOut(Now.AddMinutes(15)));
    }

    [Fact]
    public void A_successful_sign_in_forgives_earlier_mistakes()
    {
        var user = Staff();
        user.RecordFailedSignIn(Now);
        user.RecordFailedSignIn(Now);

        user.RecordSuccessfulSignIn();

        Assert.Equal(0, user.FailedSignIns);
    }

    [Fact]
    public void The_owner_cannot_be_deactivated()
    {
        var owner = User.CreateOwner(Guid.CreateVersion7(), "Zé", EmailAddress.Parse("ze@loja.com"), "hash");

        // A shop without an active owner could never manage its own team again.
        DomainAssert.Breaks(DomainErrors.OwnerCannotBeDeactivated, owner.Deactivate);
    }

    [Fact]
    public void Staff_can_be_deactivated()
    {
        var user = Staff();

        user.Deactivate();

        Assert.False(user.Active);
    }
}

public class SessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private static Session NewSession() => Session.Start(
        User.CreateStaff(Guid.CreateVersion7(), "Ana", EmailAddress.Parse("ana@loja.com"), "hash"),
        "token-hash",
        Now,
        Lifetime);

    [Fact]
    public void A_session_lives_for_its_lifetime()
    {
        var session = NewSession();

        Assert.True(session.IsActive(Now.AddDays(29)));
        Assert.False(session.IsActive(Now.AddDays(30)));
    }

    [Fact]
    public void Rotating_retires_the_old_session_and_links_it_to_the_new_one()
    {
        var session = NewSession();

        var next = session.Rotate("new-hash", Now.AddHours(1), Lifetime);

        Assert.True(session.IsRevoked);
        Assert.Equal(next.Id, session.ReplacedBy);
        Assert.True(next.IsActive(Now.AddHours(1)));
        Assert.Equal(session.UserId, next.UserId);
        Assert.Equal(session.TenantId, next.TenantId);
    }

    [Fact]
    public void Each_rotation_starts_a_fresh_lifetime()
    {
        var session = NewSession();

        var next = session.Rotate("new-hash", Now.AddDays(20), Lifetime);

        // Staying signed in while in use, and signed out after 30 idle days.
        Assert.Equal(Now.AddDays(50), next.ExpiresAt);
    }

    [Fact]
    public void Revoking_twice_keeps_the_first_moment()
    {
        var session = NewSession();

        session.Revoke(Now.AddHours(1));
        session.Revoke(Now.AddHours(2));

        Assert.Equal(Now.AddHours(1), session.RevokedAt);
    }
}
