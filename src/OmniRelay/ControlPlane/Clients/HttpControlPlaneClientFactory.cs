using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniRelay.ControlPlane.Security;
using OmniRelay.Transport.Http;

namespace OmniRelay.ControlPlane.Clients;

/// <summary>Constructs HTTP/2 + HTTP/3 clients that respect shared TLS policies.</summary>
public sealed class HttpControlPlaneClientFactory : IHttpControlPlaneClientFactory
{
    private readonly IOptionsMonitor<HttpControlPlaneClientFactoryOptions> _optionsMonitor;
    private readonly TransportTlsManager? _sharedTls;
    private readonly ILogger<HttpControlPlaneClientFactory> _logger;

    public HttpControlPlaneClientFactory(
        IOptionsMonitor<HttpControlPlaneClientFactoryOptions> optionsMonitor,
        ILogger<HttpControlPlaneClientFactory> logger,
        TransportTlsManager? sharedTls = null)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sharedTls = sharedTls;
    }

    public HttpClient CreateClient(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        var options = _optionsMonitor.CurrentValue;
        if (!options.Profiles.TryGetValue(profileName, out var profile))
        {
            throw new InvalidOperationException($"Unknown HTTP control-plane profile '{profileName}'.");
        }

        return CreateClient(profile);
    }

    public HttpClient CreateClient(HttpControlPlaneClientProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var handler = CreateHandler(profile);
        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = profile.BaseAddress
        };

        if (profile.Runtime?.RequestVersion is { } defaultVersion)
        {
            client.DefaultRequestVersion = defaultVersion;
        }

        if (profile.Runtime?.VersionPolicy is { } defaultPolicy)
        {
            client.DefaultVersionPolicy = defaultPolicy;
        }

        return client;
    }

    private SocketsHttpHandler CreateHandler(HttpControlPlaneClientProfile profile)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        if (profile.Runtime is { EnableHttp3: true })
        {
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<System.Net.Security.SslApplicationProtocol>
                {
                    System.Net.Security.SslApplicationProtocol.Http3,
                    System.Net.Security.SslApplicationProtocol.Http2,
                    System.Net.Security.SslApplicationProtocol.Http11
                }
            };
        }
        else
        {
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<System.Net.Security.SslApplicationProtocol>
                {
                    System.Net.Security.SslApplicationProtocol.Http2,
                    System.Net.Security.SslApplicationProtocol.Http11
                }
            };
        }

        ApplyClientTls(handler.SslOptions!, profile);
        return handler;
    }

    private void ApplyClientTls(System.Net.Security.SslClientAuthenticationOptions sslOptions, HttpControlPlaneClientProfile profile)
    {
        if (!profile.UseSharedTls || _sharedTls is null)
        {
            return;
        }

        var certificate = _sharedTls.GetCertificate();
        sslOptions.ClientCertificates ??= new();
        sslOptions.ClientCertificates.Add(certificate);
    }
}
