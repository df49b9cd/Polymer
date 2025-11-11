using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Core.Gossip;

/// <summary>
/// Loads and refreshes the X509 certificate used for gossip TLS.
/// </summary>
public sealed class MeshGossipCertificateProvider : IDisposable
{
    private readonly MeshGossipOptions _options;
    private readonly ILogger<MeshGossipCertificateProvider> _logger;
    private readonly object _lock = new();
    private X509Certificate2? _certificate;
    private DateTimeOffset _lastLoaded;
    private DateTime _lastWrite;

    public MeshGossipCertificateProvider(MeshGossipOptions options, ILogger<MeshGossipCertificateProvider> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Tls.CertificatePath);

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
        var path = ResolveCertificatePath();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Mesh gossip certificate '{path}' was not found.");
        }

        var raw = File.ReadAllBytes(path);
        var cert = _options.Tls.CertificatePassword is { Length: > 0 } password
            ? new X509Certificate2(raw, password, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable)
            : new X509Certificate2(raw, (string?)null, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);

        _certificate?.Dispose();
        _certificate = cert;
        _lastLoaded = DateTimeOffset.UtcNow;
        _lastWrite = File.GetLastWriteTimeUtc(path);
        _logger.LogInformation("Mesh gossip certificate loaded from {Path}. Subject={Subject}", path, cert.Subject);
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
