using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace OmniRelay.ControlPlane.Security;

/// <summary>
/// Loads and refreshes TLS certificates for control-plane transports (gRPC/HTTP/gossip).
/// Provides a single source of truth for both server and client credentials.
/// </summary>
public sealed class TransportTlsManager : IDisposable
{
    private readonly TransportTlsOptions _options;
    private readonly ILogger<TransportTlsManager> _logger;
    private readonly object _lock = new();
    private X509Certificate2? _certificate;
    private DateTimeOffset _lastLoaded;
    private DateTime _lastWrite;
    private static readonly Action<ILogger, string, string, Exception?> CertificateLoadedLog =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, "TransportCertificateLoaded"),
            "Control-plane TLS certificate loaded from {Source}. Subject={Subject}");

    public TransportTlsManager(TransportTlsOptions options, ILogger<TransportTlsManager> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Returns true when a certificate source was configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.CertificatePath) ||
        !string.IsNullOrWhiteSpace(_options.CertificateData);

    /// <summary>
    /// Retrieves the latest certificate instance, reloading from disk/inline data when necessary.
    /// The caller takes ownership over the returned <see cref="X509Certificate2"/>.
    /// </summary>
    public X509Certificate2 GetCertificate()
    {
        lock (_lock)
        {
            if (_certificate is null || ShouldReloadLocked())
            {
                ReloadLocked();
            }

            return new X509Certificate2(_certificate!);
        }
    }

    private bool ShouldReloadLocked()
    {
        if (!IsConfigured)
        {
            return false;
        }

        if (_certificate is null)
        {
            return true;
        }

        // Inline certificates are immutable until configuration reloads them.
        if (!string.IsNullOrWhiteSpace(_options.CertificateData))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (_options.ReloadInterval is { } interval &&
            interval > TimeSpan.Zero &&
            now - _lastLoaded >= interval)
        {
            return true;
        }

        var path = ResolveCertificatePath();
        if (!File.Exists(path))
        {
            return false;
        }

        var write = File.GetLastWriteTimeUtc(path);
        return write > _lastWrite;
    }

    private void ReloadLocked()
    {
        var flags = _options.KeyStorageFlags;
        X509Certificate2 certificate;
        string source;
        DateTime? lastWrite = null;

        if (!string.IsNullOrWhiteSpace(_options.CertificateData))
        {
            byte[] rawBytes;
            try
            {
                rawBytes = Convert.FromBase64String(_options.CertificateData);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("transport TLS certificate data is not valid Base64.", ex);
            }

            try
            {
                certificate = X509CertificateLoader.LoadPkcs12(rawBytes, _options.CertificatePassword, flags);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(rawBytes);
                TransportTlsManagerTestHooks.NotifySecretsCleared(rawBytes);
            }

            source = "inline certificate data";
        }
        else
        {
            var path = ResolveCertificatePath();
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Transport TLS certificate '{path}' was not found.");
            }

            var raw = File.ReadAllBytes(path);
            certificate = X509CertificateLoader.LoadPkcs12(raw, _options.CertificatePassword, flags);
            source = path;
            lastWrite = File.GetLastWriteTimeUtc(path);
        }

        _certificate?.Dispose();
        _certificate = certificate;
        _lastLoaded = DateTimeOffset.UtcNow;
        _lastWrite = lastWrite ?? DateTime.MinValue;
        CertificateLoadedLog(_logger, source, certificate.Subject, null);
    }

    private string ResolveCertificatePath()
    {
        var path = _options.CertificatePath ?? throw new InvalidOperationException("A transport TLS certificate path must be configured.");
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.Combine(AppContext.BaseDirectory, path);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _certificate?.Dispose();
            _certificate = null;
        }
    }
}

internal static class TransportTlsManagerTestHooks
{
    public static Action<byte[]>? SecretsCleared { get; set; }

    public static void NotifySecretsCleared(byte[] buffer)
    {
        SecretsCleared?.Invoke(buffer);
    }
}
