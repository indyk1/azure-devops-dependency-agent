using SampleLegacyApp.Core.Hashing;

namespace SampleLegacyApp.Tests;

/// <summary>Pins the persisted hash format: a behaviour change in the PBKDF2 code path must fail these tests.</summary>
public class PasswordHasherTests
{
    private static readonly byte[] Salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

    [Fact]
    public void Hash_matches_known_vector()
    {
        // Independently computed: hashlib.pbkdf2_hmac('sha256', password, salt, 100000, 32)
        Assert.Equal(
            "49D49C25F597846209F0D92E7770AB64E1C75E94B4CE6C509265EE67175D2A1E",
            PasswordHasher.Hash("correct horse battery staple", Salt));
    }

    [Fact]
    public void Hash_matches_known_vector_for_empty_password_and_custom_iterations()
    {
        Assert.Equal(
            "C5B301B1FD61BCED63F00642A2304EC674519FBD7CD83C4BC83DAFE1743F9855",
            PasswordHasher.Hash("", Salt, iterations: 1000));
    }

    [Fact]
    public void Hash_is_32_bytes_of_upper_case_hex()
    {
        var hash = PasswordHasher.Hash("x", Salt, 1000);
        Assert.Equal(PasswordHasher.KeyLength * 2, hash.Length);
        Assert.Equal(hash.ToUpperInvariant(), hash);
    }

    [Fact]
    public void Verify_accepts_correct_and_rejects_wrong_passwords()
    {
        var stored = PasswordHasher.Hash("hunter2", Salt, 1000);
        Assert.True(PasswordHasher.Verify("hunter2", Salt, stored, 1000));
        Assert.False(PasswordHasher.Verify("hunter3", Salt, stored, 1000));
    }

    [Fact]
    public void Different_salts_give_different_hashes()
    {
        var other = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        Assert.NotEqual(PasswordHasher.Hash("p", Salt, 1000), PasswordHasher.Hash("p", other, 1000));
    }

    [Fact]
    public void NewSalt_returns_random_16_bytes()
    {
        var a = PasswordHasher.NewSalt();
        var b = PasswordHasher.NewSalt();
        Assert.Equal(16, a.Length);
        Assert.NotEqual(a, b);
    }
}
