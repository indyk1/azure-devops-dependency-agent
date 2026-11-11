using SampleLegacyApp.Core.Security;

namespace SampleLegacyApp.Tests;

public class LegacyTokenSignerTests
{
    private static readonly byte[] Salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

    [Fact]
    public void Sign_matches_known_vector()
    {
        // Independently computed: HMAC-SHA256(key = PBKDF2-SHA1('secret', salt, 10000, 20), payload)
        Assert.Equal(
            "4D1B1019151C6DC6AB16EF4C3A112D16D542E928836F9BC6B28B70060A97C2D4",
            LegacyTokenSigner.Sign("user=42;exp=1700000000", "secret", Salt));
    }

    [Fact]
    public void Sign_depends_on_payload_and_secret()
    {
        var baseline = LegacyTokenSigner.Sign("a", "s", Salt);
        Assert.NotEqual(baseline, LegacyTokenSigner.Sign("b", "s", Salt));
        Assert.NotEqual(baseline, LegacyTokenSigner.Sign("a", "t", Salt));
    }
}
