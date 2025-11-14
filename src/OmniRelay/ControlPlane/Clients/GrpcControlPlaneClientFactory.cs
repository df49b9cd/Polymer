using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniRelay.ControlPlane.Security;
using OmniRelay.Transport.Grpc;

namespace OmniRelay.ControlPlane.Clients;

/// <summary>Constructs gRPC HTTP/3 control-plane clients with shared transport defaults.</summary>
public sealed class GrpcControlPlaneClientFactory : IGrpcControlPlaneClientFactory
{
    private readonly IOptionsMonitor<GrpcControlPlaneClientFactoryOptions> _optionsMonitor;
    private readonly TransportTlsManager? _sharedTls;
    private readonly ILogger<GrpcControlPlaneClientFactory> _logger;

    public GrpcControlPlaneClientFactory(
        IOptionsMonitor<GrpcControlPlaneClientFactoryOptions> optionsMonitor,
        ILogger<GrpcControlPlaneClientFactory> logger,
        TransportTlsManager? sharedTls = null)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sharedTls = sharedTls;
    }

    public GrpcChannel CreateChannel(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        var options = _optionsMonitor.CurrentValue;
        if (!options.Profiles.TryGetValue(profileName, out var profile))
        {
            throw new InvalidOperationException($"Unknown gRPC control-plane profile '{profileName}'.");
        }

        return CreateChannel(profile);
    }

    public GrpcChannel CreateChannel(GrpcControlPlaneClientProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var handler = CreateHandler(profile);
        var channelOptions = new GrpcChannelOptions
        {
            HttpHandler = handler
        };

        ApplyRuntimeOptions(channelOptions, profile.Runtime);
        return GrpcChannel.ForAddress(profile.Address, channelOptions);
    }

    private static void ApplyRuntimeOptions(GrpcChannelOptions options, GrpcClientRuntimeOptions? runtime)
    {
        if (runtime is null)
        {
            return;
        }

        if (runtime.MaxReceiveMessageSize is { } maxReceive)
        {
            options.MaxReceiveMessageSize = maxReceive;
        }

        if (runtime.MaxSendMessageSize is { } maxSend)
        {
            options.MaxSendMessageSize = maxSend;
        }

    }

    private SocketsHttpHandler CreateHandler(GrpcControlPlaneClientProfile profile)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            EnableMultipleHttp2Connections = true
        };

        var preferHttp3 = profile.PreferHttp3;
        handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            ApplicationProtocols = preferHttp3
                ? new List<System.Net.Security.SslApplicationProtocol> { System.Net.Security.SslApplicationProtocol.Http3, System.Net.Security.SslApplicationProtocol.Http2 }
                : new List<System.Net.Security.SslApplicationProtocol> { System.Net.Security.SslApplicationProtocol.Http2, System.Net.Security.SslApplicationProtocol.Http3 }
        };

        ApplyClientTls(handler.SslOptions, profile);

        if (profile.Runtime is { KeepAlivePingDelay: { } delay })
        {
            handler.KeepAlivePingDelay = delay;
        }

        if (profile.Runtime is { KeepAlivePingTimeout: { } timeout })
        {
            handler.KeepAlivePingTimeout = timeout;
        }

        return handler;
    }

    private void ApplyClientTls(System.Net.Security.SslClientAuthenticationOptions sslOptions, GrpcControlPlaneClientProfile profile)
    {
        var tls = profile.Tls;
        if (tls?.ClientCertificates is not null && tls.ClientCertificates.Count > 0)
        {
            sslOptions.ClientCertificates ??= new();
            sslOptions.ClientCertificates.AddRange(tls.ClientCertificates);
        }
        else if (profile.UseSharedTls && _sharedTls is not null)
        {
            var certificate = _sharedTls.GetCertificate();
            sslOptions.ClientCertificates ??= new();
            sslOptions.ClientCertificates.Add(certificate);
        }

        if (tls?.ServerCertificateValidationCallback is not null)
        {
            sslOptions.RemoteCertificateValidationCallback = tls.ServerCertificateValidationCallback;
        }

        if (tls?.EnabledProtocols is { } protocols)
        {
            sslOptions.EnabledSslProtocols = protocols;
        }

        if (tls?.CheckCertificateRevocation is { } checkRevocation)
        {
            sslOptions.CertificateRevocationCheckMode = checkRevocation
                ? System.Security.Cryptography.X509Certificates.X509RevocationMode.Online
                : System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
        }
    }
}
