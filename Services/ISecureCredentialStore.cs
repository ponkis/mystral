namespace Mystral.Services;

/// <summary>
/// Stores small application credentials outside ordinary settings files.
/// Implementations must not persist <paramref name="value"/> as plaintext.
/// </summary>
public interface ISecureCredentialStore
{
    string? Read(string key);

    void Write(string key, string value);

    void Delete(string key);
}
