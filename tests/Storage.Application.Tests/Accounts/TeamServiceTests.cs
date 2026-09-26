using Storage.Application.Accounts;
using Storage.Application.Errors;
using Storage.Application.Tests.Fakes;
using Storage.Domain.Accounts;
using Storage.Domain.Common;

namespace Storage.Application.Tests.Accounts;

public sealed class TeamServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryAccountStore _store = new();
    private readonly TeamService _service;
    private readonly User _owner;

    public TeamServiceTests()
    {
        var shop = Tenant.Create("Mercadinho do Zé");
        _owner = User.CreateOwner(shop.Id, "Zé", EmailAddress.Parse("ze@mercadinho.com"), "hash:x");
        _store.Tenants.Add(shop);
        _store.AddUser(_owner);

        var tenant = new FixedTenant(shop.Id);
        _service = new TeamService(
            new InMemoryUserRepository(_store, tenant),
            _store,
            new FakePasswordHasher(),
            tenant,
            new ManualClock(Now));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_employee_joins_the_owners_shop_as_staff()
    {
        var added = await _service.AddStaffAsync(new AddStaffRequest("Ana", "ana@mercadinho.com", "senha-da-ana"), Token);

        Assert.Equal(UserRole.Staff, added.Role);
        Assert.Equal(_owner.TenantId, _store.Users.Single(user => user.Id == added.Id).TenantId);
    }

    [Fact]
    public async Task An_e_mail_in_use_in_any_shop_is_a_conflict()
    {
        var otherShop = Guid.CreateVersion7();
        _store.AddUser(User.CreateOwner(otherShop, "Outro", EmailAddress.Parse("ana@mercadinho.com"), "hash:x"));

        // The e-mail is the login, so it is unique across the platform.
        await Refused.WithAsync(
            ErrorKind.Conflict,
            ErrorCodes.EmailTaken,
            () => _service.AddStaffAsync(new AddStaffRequest("Ana", "ana@mercadinho.com", "senha-da-ana"), Token));
    }

    [Fact]
    public async Task Deactivating_someone_signs_them_out_everywhere()
    {
        var added = await _service.AddStaffAsync(new AddStaffRequest("Ana", "ana@mercadinho.com", "senha-da-ana"), Token);
        var ana = _store.Users.Single(user => user.Id == added.Id);
        _store.Sessions.Add(Session.Start(ana, "celular", Now, TimeSpan.FromDays(30)));
        _store.Sessions.Add(Session.Start(ana, "computador", Now, TimeSpan.FromDays(30)));

        await _service.DeactivateAsync(ana.Id, Token);

        // Otherwise a fired employee keeps working until a session happens to expire.
        Assert.All(_store.Sessions, session => Assert.True(session.IsRevoked));
    }

    [Fact]
    public async Task The_owner_cannot_be_deactivated()
    {
        var refusal = await Assert.ThrowsAsync<DomainException>(() => _service.DeactivateAsync(_owner.Id, Token));

        Assert.Equal(DomainErrors.OwnerCannotBeDeactivated, refusal.Code);
    }

    [Fact]
    public async Task People_from_another_shop_are_invisible()
    {
        var stranger = User.CreateStaff(Guid.CreateVersion7(), "Estranho", EmailAddress.Parse("x@outra.com"), "hash:x");
        _store.AddUser(stranger);

        await Refused.WithAsync(
            ErrorKind.NotFound, ErrorCodes.UserNotFound, () => _service.DeactivateAsync(stranger.Id, Token));
    }

    [Fact]
    public async Task The_owner_is_listed_first()
    {
        await _service.AddStaffAsync(new AddStaffRequest("Ana", "ana@mercadinho.com", "senha-da-ana"), Token);

        var team = await _service.ListAsync(Token);

        Assert.Equal(UserRole.Owner, team[0].Role);
        Assert.Equal(2, team.Count);
    }
}
