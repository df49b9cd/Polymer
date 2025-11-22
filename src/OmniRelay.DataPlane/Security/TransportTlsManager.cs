using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hugo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using OmniRelay.Security.Secrets;
using static Hugo.Go;

namespace OmniRelay.ControlPlane.Security;

/// <summary>
/// Loads and refreshes TLS certificates for control-plane transports (gRPC/HTTP/gossip).
/// Provides a single source of truth for both server and client credentials.
/// </summary>
public sealed class TransportTlsManager : IDisposable
{
    private readonly TransportTlsOptions _options;
    private readonly ILogger<TransportTlsManager> _logger;
    private readonly ISecretProvider? _secretProvider;
    private readonly object _lock = new();
    private X509Certificate2? _certificate;
    private DateTimeOffset _lastLoaded;
    private DateTime _lastWrite;
    private IDisposable? _dataReloadRegistration;
    private IDisposable? _passwordReloadRegistration;
    private static readonly Action<ILogger, string, string, Exception?> CertificateLoadedLog =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, "TransportCertificateLoaded"),
            "Control-plane TLS certificate loaded from {Source}. Subject={Subject}");

    private static readonly Action<ILogger, string, Exception?> SecretRotationLog =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2, "TransportSecretChanged"),
            "Control-plane TLS secret {SecretDescription} changed. Certificate will reload on next access.");

    public TransportTlsManager(
        TransportTlsOptions options,
        ILogger<TransportTlsManager> logger,
        ISecretProvider? secretProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _secretProvider = secretProvider;
    }

    /// <summary>Returns true when a certificate source was configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.CertificatePath) ||
        !string.IsNullOrWhiteSpace(_options.CertificateData) ||
        !string.IsNullOrWhiteSpace(_options.CertificateDataSecret);

    /// <summary>
    /// Retrieves the latest certificate instance, reloading from disk/inline data when necessary.
    /// The caller takes ownership over the returned <see cref="X509Certificate2"/>.
    /// </summary>
    public X509Certificate2 GetCertificate()
    {
        var result = GetCertificateResult();
        if (result.IsFailure)
        {
            throw CreateCertificateException(result.Error);
        }

        return result.Value;
    }

    public Result<X509Certificate2> GetCertificateResult()
    {
        X509Certificate2? snapshot;

        lock (_lock)
        {
            if (_certificate is null || ShouldReloadLocked())
            {
                var reload = ReloadLocked();
                if (reload.IsFailure)
                {
                    return reload.CastFailure<X509Certificate2>();
                }
            }

            snapshot = _certificate;
        }

        return Result.Try(() =>
            new X509Certificate2(snapshot ?? throw new InvalidOperationException("Transport TLS certificate could not be loaded.")));
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

        // Inline certificates (including secret-backed) reload when their change tokens fire.
        if (!string.IsNullOrWhiteSpace(_options.CertificateData) ||
            !string.IsNullOrWhiteSpace(_options.CertificateDataSecret))
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

    private Result<Unit> ReloadLocked()
    {
        return ResolveCertificatePasswordResult()
            .Then(password => LoadCertificateMaterial()
                .Map(material => (Material: material, Password: password)))
            .Map(tuple =>
            {
                var material = tuple.Material;
                var certificate = ImportCertificate(material, tuple.Password);
                UpdateCertificateCache(certificate, material);
                return Unit.Value;
            });
    }

    private Result<CertificateMaterial> LoadCertificateMaterial()
    {
        return LoadInlineCertificate()
            .Then(inline =>
            {
                if (inline is InlineCertificate value)
                {
                    return Ok(new CertificateMaterial(value.Bytes, value.Source, null, Sensitive: true));
                }

                return LoadFileCertificate();
            });
    }

    private Result<InlineCertificate?> LoadInlineCertificate()
    {
        if (!string.IsNullOrWhiteSpace(_options.CertificateData))
        {
            return Result.Try<InlineCertificate?>(() =>
            {
                var bytes = DecodeBase64(_options.CertificateData);
                return new InlineCertificate(bytes, "inline certificate data");
            });
        }

        if (string.IsNullOrWhiteSpace(_options.CertificateDataSecret))
        {
            return Ok<InlineCertificate?>(null);
        }

        return AcquireSecretResult(_options.CertificateDataSecret, "transport TLS certificate data")
            .Then(secret =>
            {
                using (secret)
                {
                    try
                    {
                        RegisterSecretReload(ref _dataReloadRegistration, secret.ChangeToken, $"secret:{_options.CertificateDataSecret}");
                        var bytes = DecodeSecretBytes(secret);
                        return Ok<InlineCertificate?>(new InlineCertificate(bytes, $"secret:{_options.CertificateDataSecret}"));
                    }
                    catch (Exception ex)
                    {
                        return Err<InlineCertificate?>(Error.FromException(ex));
                    }
                }
            });
    }

    private Result<CertificateMaterial> LoadFileCertificate()
    {
        return Result.Try(() =>
        {
            var path = ResolveCertificatePath();
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Transport TLS certificate '{path}' was not found.");
            }

            var raw = File.ReadAllBytes(path);
            var write = File.GetLastWriteTimeUtc(path);
            return new CertificateMaterial(raw, path, write, Sensitive: false);
        });
    }

    private Result<string?> ResolveCertificatePasswordResult()
    {
        if (!string.IsNullOrWhiteSpace(_options.CertificatePassword))
        {
            return Ok<string?>(_options.CertificatePassword);
        }

        if (string.IsNullOrWhiteSpace(_options.CertificatePasswordSecret))
        {
            return Ok<string?>(null);
        }

        return AcquireSecretResult(_options.CertificatePasswordSecret, "transport TLS certificate password")
            .Then(secret =>
            {
                using (secret)
                {
                    try
                    {
                        RegisterSecretReload(ref _passwordReloadRegistration, secret.ChangeToken, $"secret:{_options.CertificatePasswordSecret}");
                        var password = secret.AsString();
                        if (string.IsNullOrEmpty(password))
                        {
                            return Err<string?>(Error.From($"Secret '{_options.CertificatePasswordSecret}' did not contain a TLS password.", "transport.tls.password_missing"));
                        }

                        return Ok<string?>(password);
                    }
                    catch (Exception ex)
                    {
                        return Err<string?>(Error.FromException(ex));
                    }
                }
            });
    }

    private Result<SecretValue> AcquireSecretResult(string name, string purpose)
    {
        if (_secretProvider is null)
        {
            return Err<SecretValue>(Error.From($"{purpose} references secret '{name}' but no secret provider is configured.", "transport.tls.secret_provider_missing"));
        }

        return Result.Try(() =>
        {
            var secret = _secretProvider.GetSecretSync(name);
            if (secret is null)
            {
                throw new InvalidOperationException($"Secret '{name}' required for {purpose} was not found.");
            }

            return secret;
        });
    }

    private X509Certificate2 ImportCertificate(CertificateMaterial material, string? password)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12(material.Bytes, password, _options.KeyStorageFlags);
        }
        finally
        {
            if (material.Sensitive && material.Bytes.Length > 0)
            {
                CryptographicOperations.ZeroMemory(material.Bytes);
                TransportTlsManagerTestHooks.NotifySecretsCleared(material.Bytes);
            }
        }
    }

    private void UpdateCertificateCache(X509Certificate2 certificate, CertificateMaterial material)
    {
        _certificate?.Dispose();
        _certificate = certificate;
        _lastLoaded = DateTimeOffset.UtcNow;
        _lastWrite = material.LastWrite ?? DateTime.MinValue;
        CertificateLoadedLog(_logger, material.Source, certificate.Subject, null);
    }

    private static byte[] DecodeBase64(string data)
    {
        try
        {
            return Convert.FromBase64String(data);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("transport TLS certificate data is not valid Base64.", ex);
        }
    }

    private static byte[] DecodeSecretBytes(SecretValue secret)
    {
        var text = secret.AsString();
        if (!string.IsNullOrWhiteSpace(text))
        {
            return DecodeBase64(text);
        }

        var memory = secret.AsMemory();
        if (memory.IsEmpty)
        {
            throw new InvalidOperationException("transport TLS certificate secret had no payload.");
        }

        if (MemoryMarshal.TryGetArray(memory, out var segment) &&
            segment.Array is { } array &&
            segment.Offset == 0 &&
            segment.Count == array.Length)
        {
            return array;
        }

        return memory.ToArray();
    }

    private void RegisterSecretReload(ref IDisposable? registration, IChangeToken? token, string description)
    {
        registration?.Dispose();
        if (token is null)
        {
            registration = null;
            return;
        }

        registration = token.RegisterChangeCallback(static state =>
        {
            var (manager, reason) = ((TransportTlsManager, string))state!;
            manager.HandleSecretRotation(reason);
        }, (this, description));
    }

    private void HandleSecretRotation(string description)
    {
        lock (_lock)
        {
            _certificate?.Dispose();
            _certificate = null;
            _lastLoaded = DateTimeOffset.MinValue;
            _lastWrite = DateTime.MinValue;
            SecretRotationLog(_logger, description, null);
        }
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
            _dataReloadRegistration?.Dispose();
            _dataReloadRegistration = null;
            _passwordReloadRegistration?.Dispose();
            _passwordReloadRegistration = null;
        }
    }
    private readonly record struct CertificateMaterial(byte[] Bytes, string Source, DateTime? LastWrite, bool Sensitive);

    private readonly record struct InlineCertificate(byte[] Bytes, string Source);
    private static Exception CreateCertificateException(Error? error)
    {
        if (error?.Cause is Exception cause)
        {
            return cause;
        }

        return new InvalidOperationException(error?.Message ?? "Transport TLS certificate could not be loaded.");
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
