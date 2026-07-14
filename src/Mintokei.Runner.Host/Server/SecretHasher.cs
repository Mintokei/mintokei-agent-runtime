using System.Security.Cryptography;
using System.Text;

namespace Mintokei.Runner.Host.Server;

/// <summary>
/// SHA-256 hash of a secret/token, base64-encoded — how enrollment tokens and runner secrets are
/// stored and compared.
/// </summary>
public static class SecretHasher
{
    public static string Hash(string plaintext)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToBase64String(bytes);
    }
}
