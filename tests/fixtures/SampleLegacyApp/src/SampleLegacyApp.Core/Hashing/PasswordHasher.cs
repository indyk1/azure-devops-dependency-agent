using System.Security.Cryptography;

namespace SampleLegacyApp.Core.Hashing;

/// <summary>
/// PBKDF2-SHA256 password hashing for the sample app's own user store.
/// The output format (upper-case hex of the 32-byte derived key) is persisted, so it must never change.
/// </summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 100_000;
    public const int KeyLength = 32;

    public static string Hash(string password, byte[] salt, int iterations = DefaultIterations)
    {
        using var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return Convert.ToHexString(kdf.GetBytes(KeyLength));
    }

    public static bool Verify(string password, byte[] salt, string expectedHex, int iterations = DefaultIterations)
    {
        var actual = Convert.FromHexString(Hash(password, salt, iterations));
        var expected = Convert.FromHexString(expectedHex);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(16);
}
