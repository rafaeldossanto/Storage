namespace Storage.Application.Abstractions;

public enum PasswordVerification
{
    Failed,
    Succeeded,

    /// <summary>
    /// Right password, stored with parameters that are now too weak. The caller re-hashes
    /// it on the spot - the only moment the plain password is in hand.
    /// </summary>
    SucceededNeedsRehash,
}

public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerification Verify(string passwordHash, string password);

    /// <summary>
    /// Takes as long as a real <see cref="Verify"/> without having an account to check. Used
    /// when the e-mail matches nobody, so a miss is not faster than a wrong password.
    /// </summary>
    void VerifyDecoy(string password);
}
