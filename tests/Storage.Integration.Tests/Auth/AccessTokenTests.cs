using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Storage.Api.Auth;
using Storage.Domain.Accounts;

namespace Storage.Integration.Tests.Auth;

/// <summary>
/// Issues real tokens and checks them with the exact parameters the API validates with.
/// </summary>
public sealed class AccessTokenTests
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly AuthSettings _settings = SettingsWith(RandomNumberGenerator.GetBytes(32));
    private readonly User _staff =
        User.CreateStaff(Guid.CreateVersion7(), "Ana", EmailAddress.Parse("ana@mercadinho.com"), "hash");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_token_carries_the_shop_and_the_role()
    {
        var issued = new JwtAccessTokenIssuer(_settings, TimeProvider.System).Issue(_staff);

        var result = await ValidateAsync(issued.Value, _settings);

        Assert.True(result.IsValid);
        Assert.Equal(_staff.TenantId.ToString(), result.Claims[StorageClaims.Tenant]);
        Assert.Equal("Staff", result.Claims[StorageClaims.Role]);
        Assert.Equal(_staff.Id.ToString(), result.Claims[StorageClaims.Subject]);
    }

    [Fact]
    public async Task A_token_lives_fifteen_minutes()
    {
        var issued = new JwtAccessTokenIssuer(_settings, TimeProvider.System).Issue(_staff);

        Assert.InRange(
            issued.ExpiresAt - DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(14),
            TimeSpan.FromMinutes(15));

        Assert.True((await ValidateAsync(issued.Value, _settings)).IsValid);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var anHourAgo = new FixedClock(DateTimeOffset.UtcNow.AddHours(-1));
        var issued = new JwtAccessTokenIssuer(_settings, anHourAgo).Issue(_staff);

        Assert.False((await ValidateAsync(issued.Value, _settings)).IsValid);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_refused()
    {
        var forger = SettingsWith(RandomNumberGenerator.GetBytes(32));
        var forged = new JwtAccessTokenIssuer(forger, TimeProvider.System).Issue(_staff);

        Assert.False((await ValidateAsync(forged.Value, _settings)).IsValid);
    }

    [Fact]
    public async Task An_unsigned_token_is_refused()
    {
        // "alg": "none" - the classic way to forge a JWT against a careless validator.
        var unsigned = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _settings.Issuer,
            Audience = _settings.Audience,
            Expires = DateTime.UtcNow.AddMinutes(15),
            Claims = new Dictionary<string, object>
            {
                [StorageClaims.Tenant] = Guid.CreateVersion7().ToString(),
                [StorageClaims.Role] = "Owner",
            },
        });

        Assert.False((await ValidateAsync(unsigned, _settings)).IsValid);
    }

    [Fact]
    public void A_signing_key_shorter_than_256_bits_is_refused_at_start_up()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => AuthSettings.From(configuration, allowEphemeralKey: false, secureCookies: true));
    }

    [Fact]
    public void Production_refuses_to_start_without_a_signing_key()
    {
        var empty = new ConfigurationBuilder().Build();

        // A key generated on the fly would sign every customer out on each deploy.
        Assert.Throws<InvalidOperationException>(
            () => AuthSettings.From(empty, allowEphemeralKey: false, secureCookies: true));
    }

    private Task<TokenValidationResult> ValidateAsync(string token, AuthSettings settings) =>
        _handler.ValidateTokenAsync(token, JwtAccessTokenIssuer.ValidationParameters(settings));

    private static AuthSettings SettingsWith(byte[] key) => new(
        "storage-api",
        "storage-front",
        new SymmetricSecurityKey(key),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromDays(30),
        SecureCookies: true);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
