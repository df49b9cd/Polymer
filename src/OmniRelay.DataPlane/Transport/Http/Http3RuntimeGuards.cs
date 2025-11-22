using System.Net.Quic;
using System.Security.Cryptography.X509Certificates;

namespace OmniRelay.Transport.Http;

/// <summary>
/// Guards that validate environment prerequisites for enabling HTTP/3 (QUIC) on the inbound server.
/// Ensures MsQuic availability, TLS 1.3 support, and a valid certificate with private key.
/// </summary>
internal static class Http3RuntimeGuards
{
    public static void EnsureServerSupport(string endpointDescription, X509Certificate2? certificate)
    {
        if (!QuicListener.IsSupported)
        {
            throw new InvalidOperationException(
                $"HTTP/3 was requested for '{endpointDescription}' but the current runtime does not have an MsQuic implementation available. Install the required MsQuic dependencies or disable HTTP/3 for this listener.");
        }

        if (!IsTls13Supported())
        {
            throw new InvalidOperationException(
                $"HTTP/3 was requested for '{endpointDescription}' but TLS 1.3 is not available on this operating system. Upgrade the host OS or disable HTTP/3 for this listener.");
        }

        if (certificate is null)
        {
            throw new InvalidOperationException(
                $"HTTP/3 requires HTTPS with a TLS certificate. Configure a TLS certificate for '{endpointDescription}' or disable HTTP/3.");
        }

        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException(
                $"The TLS certificate configured for '{endpointDescription}' is missing a private key. HTTP/3 requires a certificate with a private key to complete the TLS 1.3 handshake.");
        }
    }

    private static bool IsTls13Supported()
    {
        if (OperatingSystem.IsWindows())
        {
            return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
        }

        if (OperatingSystem.IsLinux())
        {
            return true;
        }

        return false;
    }
}
