using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SampleLegacyApp.Core.Certificates;

namespace SampleLegacyApp.Tests;

public class CertificateReaderTests
{
    /// <summary>Generates a self-signed public certificate (DER) in-test, so no key material is committed.</summary>
    private static byte[] CreateCertificate(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter, out string thumbprint)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        thumbprint = certificate.Thumbprint;
        return certificate.Export(X509ContentType.Cert);
    }

    [Fact]
    public void Load_returns_the_same_certificate()
    {
        var der = CreateCertificate("CN=sample-partner", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), out var thumbprint);

        using var loaded = CertificateReader.Load(der);

        Assert.Equal("CN=sample-partner", loaded.Subject);
        Assert.Equal(thumbprint, loaded.Thumbprint);
        Assert.False(loaded.HasPrivateKey);
    }

    [Fact]
    public void Describe_includes_subject_and_thumbprint()
    {
        var der = CreateCertificate("CN=describe-me", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), out var thumbprint);
        Assert.Equal($"CN=describe-me [{thumbprint}]", CertificateReader.Describe(der));
    }

    [Fact]
    public void IsExpired_compares_not_after_with_now()
    {
        var der = CreateCertificate("CN=expiry", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(10), out _);
        Assert.False(CertificateReader.IsExpired(der, DateTimeOffset.UtcNow));
        Assert.True(CertificateReader.IsExpired(der, DateTimeOffset.UtcNow.AddDays(20)));
    }

    [Fact]
    public void Load_throws_for_garbage()
    {
        Assert.ThrowsAny<CryptographicException>(() => CertificateReader.Load([1, 2, 3, 4]));
    }
}
