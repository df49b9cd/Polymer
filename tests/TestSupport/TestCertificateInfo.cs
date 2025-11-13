using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OmniRelay.Tests.Support;

internal sealed record TestCertificateInfo(string Subject, string Password, string CertificateData)
{
    public X509Certificate2 CreateCertificate()
    {
        if (string.IsNullOrWhiteSpace(CertificateData))
        {
            throw new InvalidOperationException("Certificate data is not initialized.");
        }

        byte[] rawBytes;
        try
        {
            rawBytes = Convert.FromBase64String(CertificateData);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Certificate data is not valid Base64.", ex);
        }

        try
        {
#pragma warning disable SYSLIB0057
            return string.IsNullOrEmpty(Password)
                ? new X509Certificate2(rawBytes)
                : new X509Certificate2(rawBytes, Password, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawBytes);
        }
    }
}
