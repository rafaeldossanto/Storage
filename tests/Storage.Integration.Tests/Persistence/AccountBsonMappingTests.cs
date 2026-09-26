using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Storage.Domain.Accounts;
using Storage.Infrastructure.Persistence;

namespace Storage.Integration.Tests.Persistence;

public sealed class AccountBsonMappingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    public AccountBsonMappingTests() => StorageBsonSerialization.Register();

    private static User Owner() =>
        User.CreateOwner(Guid.CreateVersion7(), "Zé", EmailAddress.Parse("Ze@Mercadinho.com"), "hash-value");

    [Fact]
    public void The_e_mail_is_stored_normalised_so_the_unique_index_compares_like_the_application()
    {
        var document = Owner().ToBsonDocument();

        Assert.Equal("ze@mercadinho.com", document["Email"].AsString);
    }

    [Fact]
    public void The_role_is_stored_by_name()
    {
        Assert.Equal("Owner", Owner().ToBsonDocument()["Role"].AsString);
    }

    [Fact]
    public void A_session_expiry_is_a_real_date_because_the_TTL_index_needs_one()
    {
        var session = Session.Start(Owner(), "token-hash", Now, TimeSpan.FromDays(30));

        var document = session.ToBsonDocument();

        // Stored as text like the other timestamps, the TTL monitor would ignore it and
        // expired sessions would pile up forever.
        Assert.Equal(BsonType.DateTime, document["ExpiresAt"].BsonType);
        Assert.Equal(BsonType.String, document["CreatedAt"].BsonType);
    }

    [Fact]
    public void A_user_survives_a_round_trip()
    {
        var owner = Owner();
        owner.RecordFailedSignIn(Now);
        owner.MarkCreated(Now);

        var restored = BsonSerializer.Deserialize<User>(owner.ToBsonDocument());

        Assert.Equal(owner.Id, restored.Id);
        Assert.Equal(owner.TenantId, restored.TenantId);
        Assert.Equal(owner.Email, restored.Email);
        Assert.Equal(owner.PasswordHash, restored.PasswordHash);
        Assert.Equal(UserRole.Owner, restored.Role);
        Assert.Equal(1, restored.FailedSignIns);
        Assert.Equal(Now, restored.CreatedAt);
    }

    [Fact]
    public void A_rotated_session_survives_a_round_trip()
    {
        var session = Session.Start(Owner(), "token-hash", Now, TimeSpan.FromDays(30));
        var next = session.Rotate("next-hash", Now.AddHours(1), TimeSpan.FromDays(30));

        var restored = BsonSerializer.Deserialize<Session>(session.ToBsonDocument());

        Assert.Equal(session.ExpiresAt, restored.ExpiresAt);
        Assert.True(restored.IsRevoked);
        Assert.Equal(next.Id, restored.ReplacedBy);
    }

    [Fact]
    public void A_shop_survives_a_round_trip_with_its_time_zone()
    {
        var shop = Tenant.Create("Mercadinho do Zé", "America/Manaus");

        var restored = BsonSerializer.Deserialize<Tenant>(shop.ToBsonDocument());

        Assert.Equal("America/Manaus", restored.TimeZoneId);
        Assert.True(restored.Active);
    }
}
