using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Core.Gossip;

/// <summary>
/// Loads and refreshes the X509 certificate used for gossip TLS.
/// </summary>
public sealed class MeshGossipCertificateProvider(
    MeshGossipOptions options,
    ILogger<MeshGossipCertificateProvider> logger)
    : IDisposable
{
    private readonly MeshGossipOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<MeshGossipCertificateProvider> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly object _lock = new();
    private X509Certificate2? _certificate;
    private DateTimeOffset _lastLoaded;
    private DateTime _lastWrite;
    private static readonly Action<ILogger, string, string, Exception?> CertificateLoadedLog =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, "MeshGossipCertificateLoaded"),
            "Mesh gossip certificate loaded from {Path}. Subject={Subject}");

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Tls.CertificatePath) ||
        !string.IsNullOrWhiteSpace(_options.Tls.CertificateData);

    public X509Certificate2 GetCertificate()
    {
        lock (_lock)
        {
            if (_certificate is null || ShouldReloadLocked())
            {
                ReloadLocked();
            }

            return _certificate!;
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

        if (!string.IsNullOrWhiteSpace(_options.Tls.CertificateData))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var reloadInterval = _options.Tls.ReloadIntervalOverride ?? _options.CertificateReloadInterval;
        if (reloadInterval > TimeSpan.Zero && now - _lastLoaded >= reloadInterval)
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
        var flags = X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable;
        X509Certificate2 cert;
        string source;
        DateTime? lastWrite = null;

        if (!string.IsNullOrWhiteSpace(_options.Tls.CertificateData))
        {
            byte[] rawBytes;
            try
            {
                rawBytes = Convert.FromBase64String(_options.Tls.CertificateData);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("mesh:gossip:tls:certificateData is not valid Base64.", ex);
            }

        try
        {
            cert = X509CertificateLoader.LoadPkcs12(rawBytes, _options.Tls.CertificatePassword, flags);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawBytes);
            MeshGossipCertificateProviderTestHooks.NotifySecretsCleared(rawBytes);
        }

            source = "inline certificate data";
        }
        else
        {
            var path = ResolveCertificatePath();
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Mesh gossip certificate '{path}' was not found.");
            }

            var raw = File.ReadAllBytes(path);
            cert = X509CertificateLoader.LoadPkcs12(raw, _options.Tls.CertificatePassword, flags);
            source = path;
            lastWrite = File.GetLastWriteTimeUtc(path);
        }

        _certificate?.Dispose();
        _certificate = cert;
        _lastLoaded = DateTimeOffset.UtcNow;
        _lastWrite = lastWrite ?? DateTime.MinValue;
        CertificateLoadedLog(_logger, source, cert.Subject, null);
    }

    private string ResolveCertificatePath()
    {
        var path = _options.Tls.CertificatePath ?? throw new InvalidOperationException("mesh:gossip:tls:certificatePath must be configured.");
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

internal static class MeshGossipCertificateProviderTestHooks
{
    public static Action<byte[]>? SecretsCleared { get; set; }

    public static void NotifySecretsCleared(byte[] buffer)
    {
        SecretsCleared?.Invoke(buffer);
    }
}
