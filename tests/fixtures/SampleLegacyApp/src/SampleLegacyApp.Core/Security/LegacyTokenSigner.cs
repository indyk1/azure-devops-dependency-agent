using System.Security.Cryptography;
using System.Text;

namespace SampleLegacyApp.Core.Security;

/// <summary>
/// Signs short-lived tokens with a key derived from a shared secret.
/// Lives in a "Security" folder, which is a protected path by default: code fixers must not edit it,
/// so after the upgrade this file keeps its obsolete API usage and the run ends as a draft PR.
/// </summary>
public static class LegacyTokenSigner
{
    public static string Sign(string payload, string secret, byte[] salt)
    {
        using var kdf = new Rfc2898DeriveBytes(secret, salt, 10_000, HashAlgorithmName.SHA1);
        var key = kdf.GetBytes(20);
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}
