#pragma warning disable IDE0005
using System.Net;
using System.Net.Security;
using Microsoft.Extensions.Logging;
#pragma warning restore IDE0005
using Hugo;
using Microsoft.Extensions.Options;
using OmniRelay.Identity;
using static Hugo.Go;

namespace OmniRelay.ControlPlane.Clients;

/// <summary>Constructs HTTP/2 + HTTP/3 clients that respect shared TLS policies.</summary>
public sealed class HttpControlPlaneClientFactory : IHttpControlPlaneClientFactory
{
    private readonly IOptionsMonitor<HttpControlPlaneClientFactoryOptions> _optionsMonitor;
    private readonly TransportTlsManager? _sharedTls;
    private readonly ILogger<HttpControlPlaneClientFactory> _logger;
    private static readonly Action<ILogger, string, string?, Exception?> HttpProfileFailureLog =
        LoggerMessage.Define<string, string?>(
            LogLevel.Error,
            new EventId(1, "HttpControlPlaneProfileFailure"),
            "Failed to create HTTP control-plane client for profile {Profile}: {Message}");
    private static readonly Action<ILogger, Uri, string?, Exception?> HttpEndpointFailureLog =
        LoggerMessage.Define<Uri, string?>(
            LogLevel.Error,
            new EventId(2, "HttpControlPlaneEndpointFailure"),
            "Failed to create HTTP control-plane client for {BaseAddress}: {Message}");

    public HttpControlPlaneClientFactory(
        IOptionsMonitor<HttpControlPlaneClientFactoryOptions> optionsMonitor,
        ILogger<HttpControlPlaneClientFactory> logger,
        TransportTlsManager? sharedTls = null)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sharedTls = sharedTls;
    }

    public Result<HttpClient> CreateClient(string profileName, CancellationToken cancellationToken = default)
    {
        var result = ResolveProfile(profileName)
            .Then(profile => CreateClient(profile, cancellationToken));

        if (result.IsFailure)
        {
            HttpProfileFailureLog(_logger, profileName, result.Error?.Message ?? "unknown", null);
        }

        return result;
    }

    public Result<HttpClient> CreateClient(HttpControlPlaneClientProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var result = InitializeHandler(profile, cancellationToken)
            .Then(tuple => BuildHttpClient(tuple.Profile, tuple.Handler, tuple.RuntimeSnapshot));

        if (result.IsFailure)
        {
            HttpEndpointFailureLog(_logger, profile.BaseAddress, result.Error?.Message ?? "unknown", null);
        }

        return result;
    }

    private Result<HttpControlPlaneClientProfile> ResolveProfile(string profileName)
    {
        return Go.Ok(profileName)
            .Ensure(name => !string.IsNullOrWhiteSpace(name), _ => Error.From("A profile name must be provided.", ErrorCodes.Validation))
            .Map(name => (Name: name, Options: _optionsMonitor.CurrentValue))
            .Ensure(tuple => tuple.Options.Profiles.ContainsKey(tuple.Name), tuple => Error.From($"Unknown HTTP control-plane profile '{tuple.Name}'.", ErrorCodes.Validation).WithMetadata("profile", tuple.Name))
            .Map(tuple => tuple.Options.Profiles[tuple.Name]);
    }

    private Result<(HttpControlPlaneClientProfile Profile, SocketsHttpHandler Handler, HttpRuntimeSnapshot RuntimeSnapshot)> InitializeHandler(HttpControlPlaneClientProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            };

            var tlsTask = BuildSslOptionsAsync(profile, cancellationToken);
            var runtimeTask = BuildRuntimeSnapshotAsync(profile, cancellationToken);

            System.Net.Security.SslClientAuthenticationOptions? sslOptions = null;
            HttpRuntimeSnapshot runtimeSnapshot = default;

            using var group = new ErrGroup(cancellationToken);

            group.Go(async (_, token) =>
            {
                var tlsResult = await tlsTask.ConfigureAwait(false);
                if (tlsResult.IsFailure)
                {
                    return tlsResult.CastFailure<Unit>();
                }

                sslOptions = tlsResult.Value;
                return Ok(Unit.Value);
            });

            group.Go(async (_, token) =>
            {
                var runtimeResult = await runtimeTask.ConfigureAwait(false);
                if (runtimeResult.IsFailure)
                {
                    return runtimeResult.CastFailure<Unit>();
                }

                runtimeSnapshot = runtimeResult.Value;
                return Ok(Unit.Value);
            });

            var completion = group.WaitAsync(cancellationToken).GetAwaiter().GetResult();
            if (completion.IsFailure)
            {
                return completion.CastFailure<(HttpControlPlaneClientProfile, SocketsHttpHandler, HttpRuntimeSnapshot)>();
            }

            handler.SslOptions = sslOptions;
            return Ok((profile, handler, runtimeSnapshot));
        }
        catch (Exception ex)
        {
            return Result.Fail<(HttpControlPlaneClientProfile, SocketsHttpHandler, HttpRuntimeSnapshot)>(Error.FromException(ex));
        }
    }

    private static Result<HttpClient> BuildHttpClient(HttpControlPlaneClientProfile profile, SocketsHttpHandler handler, HttpRuntimeSnapshot runtimeSnapshot)
    {
        return Result.Try(() =>
        {
            var client = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = profile.BaseAddress
            };

            if (runtimeSnapshot.RequestVersion is { } version)
            {
                client.DefaultRequestVersion = version;
            }

            if (runtimeSnapshot.VersionPolicy is { } policy)
            {
                client.DefaultVersionPolicy = policy;
            }

            return client;
        });
    }

    private ValueTask<Result<System.Net.Security.SslClientAuthenticationOptions>> BuildSslOptionsAsync(HttpControlPlaneClientProfile profile, CancellationToken cancellationToken)
    {
        return new(Task.Run(() =>
        {
            return Result.Try(() =>
            {
                var enableHttp3 = profile.Runtime?.EnableHttp3 == true;
                var options = new System.Net.Security.SslClientAuthenticationOptions
                {
                    ApplicationProtocols = BuildApplicationProtocols(enableHttp3)
                };

                ApplyClientTls(options, profile);
                return options;
            });
        }, cancellationToken));
    }

    private static ValueTask<Result<HttpRuntimeSnapshot>> BuildRuntimeSnapshotAsync(HttpControlPlaneClientProfile profile, CancellationToken cancellationToken)
    {
        return new(Task.Run(() =>
        {
            var runtime = profile.Runtime;
            var snapshot = new HttpRuntimeSnapshot(runtime?.RequestVersion, runtime?.VersionPolicy);
            return Ok(snapshot);
        }, cancellationToken));
    }

    private static List<SslApplicationProtocol> BuildApplicationProtocols(bool enableHttp3) =>
        enableHttp3
            ? new List<SslApplicationProtocol>
            {
                SslApplicationProtocol.Http3,
                SslApplicationProtocol.Http2,
                SslApplicationProtocol.Http11
            }
            : new List<SslApplicationProtocol>
            {
                SslApplicationProtocol.Http2,
                SslApplicationProtocol.Http11
            };

    private void ApplyClientTls(SslClientAuthenticationOptions sslOptions, HttpControlPlaneClientProfile profile)
    {
        if (!profile.UseSharedTls || _sharedTls is null)
        {
            return;
        }

        var certificate = _sharedTls.GetCertificate();
        sslOptions.ClientCertificates ??= new();
        sslOptions.ClientCertificates.Add(certificate);
    }

    private readonly record struct HttpRuntimeSnapshot(Version? RequestVersion, HttpVersionPolicy? VersionPolicy);
}
