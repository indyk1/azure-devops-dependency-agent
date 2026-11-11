using System.Security.Cryptography.X509Certificates;

namespace SampleLegacyApp.Core.Certificates;

/// <summary>Reads DER/PEM-encoded public certificates (no private key) supplied by partners.</summary>
public static class CertificateReader
{
    public static X509Certificate2 Load(byte[] rawData) => new X509Certificate2(rawData);

    public static string Describe(byte[] rawData)
    {
        using var certificate = Load(rawData);
        return $"{certificate.Subject} [{certificate.Thumbprint}]";
    }

    public static bool IsExpired(byte[] rawData, DateTimeOffset now)
    {
        using var certificate = Load(rawData);
        return certificate.NotAfter.ToUniversalTime() < now.UtcDateTime;
    }
}
