using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hugo;
using Microsoft.Extensions.Logging;
using OmniRelay.Identity;
using OmniRelay.Security.Secrets;
using static Hugo.Go;

namespace OmniRelay.Identity;

public sealed class TransportTlsManager : IDisposable
{
    private readonly TransportTlsOptions _options;
    private readonly ILogger<TransportTlsManager> _logger;
    private readonly ISecretProvider? _secretProvider;
    private X509Certificate2? _certificate;
    private TransportCertificateMaterial _material;

    public TransportTlsManager(TransportTlsOptions options, ILogger<TransportTlsManager> logger, ISecretProvider? secretProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _secretProvider = secretProvider;
        _material = new TransportCertificateMaterial(Array.Empty<byte>(), null, DateTimeOffset.MinValue);
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.CertificatePath)
        || !string.IsNullOrWhiteSpace(_options.CertificateData)
        || !string.IsNullOrWhiteSpace(_options.CertificateDataSecret);

    public static Result<TransportTlsManager> TryCreate(TransportTlsOptions options, ILogger<TransportTlsManager> logger, ISecretProvider? secretProvider = null)
    {
        try
        {
            return Ok(new TransportTlsManager(options, logger, secretProvider));
        }
        catch (Exception ex)
        {
            return Err<TransportTlsManager>(Error.FromException(ex).WithMetadata("transport.tls", "init"));
        }
    }

    /// <summary>The caller takes ownership over the returned <see cref="X509Certificate2"/>.</summary>
    public X509Certificate2 GetCertificate()
    {
        var result = GetCertificateResult();
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error?.Message ?? "Transport TLS certificate has not been loaded.");
        }

        return result.Value;
    }

    public Result<X509Certificate2> GetCertificateResult()
    {
        var material = LoadMaterial();
        if (material.IsFailure)
        {
            return material.CastFailure<X509Certificate2>();
        }

        var imported = ImportCertificate(material.Value, material.Value.Password);
        if (_certificate is null || !string.Equals(_certificate.Thumbprint, imported.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            UpdateCertificateCache(imported, material.Value);
        }
        else
        {
            imported.Dispose();
        }

        return Ok(new X509Certificate2(_certificate));
    }

    private Result<TransportCertificateMaterial> LoadMaterial()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.CertificateData))
            {
                var bytes = Convert.FromBase64String(_options.CertificateData);
                return Ok(new TransportCertificateMaterial(bytes, ResolvePassword(), DateTimeOffset.UtcNow));
            }

            if (!string.IsNullOrWhiteSpace(_options.CertificateDataSecret))
            {
                if (_secretProvider is null)
                {
                    return Err<TransportCertificateMaterial>(Error.From("Certificate data secret configured but no secret provider available.", "transport.tls.secret_provider"));
                }

                using var secret = _secretProvider.GetSecretAsync(_options.CertificateDataSecret!, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                var raw = secret?.AsString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return Err<TransportCertificateMaterial>(Error.From("Certificate data secret returned no value.", "transport.tls.secret_missing"));
                }

                var bytes = Convert.FromBase64String(raw);
                return Ok(new TransportCertificateMaterial(bytes, ResolvePassword(), DateTimeOffset.UtcNow));
            }

            if (!string.IsNullOrWhiteSpace(_options.CertificatePath))
            {
                if (!File.Exists(_options.CertificatePath))
                {
                    return Err<TransportCertificateMaterial>(Error.From("Transport TLS certificate path is required.", "transport.tls.path"));
                }

                var bytes = File.ReadAllBytes(_options.CertificatePath);
                var ts = File.GetLastWriteTimeUtc(_options.CertificatePath);
                return Ok(new TransportCertificateMaterial(bytes, ResolvePassword(), ts));
            }

            return Err<TransportCertificateMaterial>(Error.From("Transport TLS certificate path is required.", "transport.tls.path"));
        }
        catch (FormatException ex)
        {
            return Err<TransportCertificateMaterial>(Error.From("Invalid Base64 certificate data.", "transport.tls.base64"));
        }
        catch (Exception ex)
        {
            return Err<TransportCertificateMaterial>(Error.FromException(ex).WithMetadata("transport.tls", "load"));
        }
    }

    private X509Certificate2 ImportCertificate(TransportCertificateMaterial material, string? password)
    {
        return X509CertificateLoader.LoadPkcs12(material.Certificate, password, _options.KeyStorageFlags);
    }

    private void UpdateCertificateCache(X509Certificate2 certificate, TransportCertificateMaterial material)
    {
        _certificate?.Dispose();
        _certificate = certificate;
        if (material.Certificate.Length > 0)
        {
            CryptographicOperations.ZeroMemory(material.Certificate);
            TransportTlsManagerTestHooks.NotifySecretsCleared(material.Certificate);
        }

        _material = material;
    }

    private string? ResolvePassword()
    {
        if (!string.IsNullOrWhiteSpace(_options.CertificatePassword))
        {
            return _options.CertificatePassword;
        }

        if (!string.IsNullOrWhiteSpace(_options.CertificatePasswordSecret) && _secretProvider is not null)
        {
            using var secret = _secretProvider.GetSecretAsync(_options.CertificatePasswordSecret!, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return secret?.AsString();
        }

        return null;
    }

    public void Dispose()
    {
        _certificate?.Dispose();
    }
}

public static class TransportTlsManagerTestHooks
{
    public static Action<byte[]>? SecretsCleared;

    public static void NotifySecretsCleared(byte[] buffer)
    {
        SecretsCleared?.Invoke(buffer);
    }
}

internal readonly record struct TransportCertificateMaterial(byte[] Certificate, string? Password, DateTimeOffset Timestamp);
