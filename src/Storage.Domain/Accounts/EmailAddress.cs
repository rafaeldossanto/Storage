using Storage.Domain.Common;

namespace Storage.Domain.Accounts;

/// <summary>
/// An e-mail address, trimmed and lower-cased.
/// </summary>
/// <remarks>
/// The address is the login, and it is unique across the whole platform - it is typed
/// before anyone knows which shop the person belongs to. Normalising it here is what stops
/// "Rafael@Loja.com" and "rafael@loja.com" from becoming two accounts. The check is
/// deliberately loose: the only real proof an address works is mail arriving at it.
/// </remarks>
public readonly record struct EmailAddress
{
    public const int MaxLength = 254;

    private EmailAddress(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? input, out EmailAddress email)
    {
        email = default;

        var candidate = input?.Trim();

        if (string.IsNullOrEmpty(candidate)
            || candidate.Length > MaxLength
            || candidate.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var at = candidate.IndexOf('@');

        if (at <= 0 || at != candidate.LastIndexOf('@') || at == candidate.Length - 1)
        {
            return false;
        }

        var domain = candidate[(at + 1)..];

        if (!domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.'))
        {
            return false;
        }

        email = new EmailAddress(candidate.ToLowerInvariant());
        return true;
    }

    public static EmailAddress Parse(string? input) =>
        TryParse(input, out var email)
            ? email
            : throw new DomainException(DomainErrors.EmailInvalid, "Not a valid e-mail address.");

    public override string ToString() => Value;
}
