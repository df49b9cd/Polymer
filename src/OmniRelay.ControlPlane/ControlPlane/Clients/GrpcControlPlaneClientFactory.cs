#pragma warning disable IDE0005
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Hugo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniRelay.ControlPlane.Security;
using static Hugo.Go;

namespace OmniRelay.ControlPlane.Clients;

/// <summary>Constructs gRPC HTTP/3 control-plane clients with shared transport defaults.</summary>
public sealed class GrpcControlPlaneClientFactory : IGrpcControlPlaneClientFactory
{
    private readonly IOptionsMonitor<GrpcControlPlaneClientFactoryOptions> _optionsMonitor;
    private readonly TransportTlsManager? _sharedTls;
    private readonly ILogger<GrpcControlPlaneClientFactory> _logger;
    private static readonly Action<ILogger, string, string?, Exception?> GrpcProfileFailureLog =
        LoggerMessage.Define<string, string?>(
            LogLevel.Error,
            new EventId(1, "GrpcControlPlaneProfileFailure"),
            "Failed to create gRPC control-plane channel for profile {Profile}: {Message}");
    private static readonly Action<ILogger, Uri, string?, Exception?> GrpcEndpointFailureLog =
        LoggerMessage.Define<Uri, string?>(
            LogLevel.Error,
            new EventId(2, "GrpcControlPlaneEndpointFailure"),
            "Failed to create gRPC control-plane channel for {Address}: {Message}");

    public GrpcControlPlaneClientFactory(
        IOptionsMonitor<GrpcControlPlaneClientFactoryOptions> optionsMonitor,
        ILogger<GrpcControlPlaneClientFactory> logger,
        TransportTlsManager? sharedTls = null)
    {
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sharedTls = sharedTls;
    }

    public Result<GrpcChannel> CreateChannel(string profileName, CancellationToken cancellationToken = default)
    {
        var result = ResolveProfile(profileName)
            .Then(profile => CreateChannel(profile, cancellationToken));

        if (result.IsFailure)
        {
            GrpcProfileFailureLog(_logger, profileName, result.Error?.Message ?? "unknown", null);
        }

        return result;
    }

    public Result<GrpcChannel> CreateChannel(GrpcControlPlaneClientProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var result = InitializeHandler(profile, cancellationToken)
            .Then(tuple => BuildChannel(profile, tuple.Handler, tuple.RuntimeSnapshot));

        if (result.IsFailure)
        {
            GrpcEndpointFailureLog(_logger, profile.Address, result.Error?.Message ?? "unknown", null);
        }

        return result;
    }

    private Result<GrpcControlPlaneClientProfile> ResolveProfile(string profileName)
    {
        return Go.Ok(profileName)
            .Ensure(name => !string.IsNullOrWhiteSpace(name), _ => Error.From("A profile name must be provided.", ErrorCodes.Validation))
            .Map(name => (Name: name, Options: _optionsMonitor.CurrentValue))
            .Ensure(tuple => tuple.Options.Profiles.ContainsKey(tuple.Name), tuple => Error.From($"Unknown gRPC control-plane profile '{tuple.Name}'.", ErrorCodes.Validation).WithMetadata("profile", tuple.Name))
            .Map(tuple => tuple.Options.Profiles[tuple.Name]);
    }

    private Result<(SocketsHttpHandler Handler, GrpcRuntimeSnapshot RuntimeSnapshot)> InitializeHandler(GrpcControlPlaneClientProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                EnableMultipleHttp2Connections = true
            };

            var tlsTask = BuildSslOptionsAsync(profile, cancellationToken);
            var runtimeTask = BuildRuntimeSnapshotAsync(profile, cancellationToken);

            System.Net.Security.SslClientAuthenticationOptions? sslOptions = null;
            GrpcRuntimeSnapshot runtimeSnapshot = default;

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
                return completion.CastFailure<(SocketsHttpHandler, GrpcRuntimeSnapshot)>();
            }

            handler.SslOptions = sslOptions;
            ApplyHandlerRuntime(handler, runtimeSnapshot);
            return Ok((handler, runtimeSnapshot));
        }
        catch (Exception ex)
        {
            return Result.Fail<(SocketsHttpHandler, GrpcRuntimeSnapshot)>(Error.FromException(ex));
        }
    }

    private static Result<GrpcChannel> BuildChannel(GrpcControlPlaneClientProfile profile, SocketsHttpHandler handler, GrpcRuntimeSnapshot runtimeSnapshot)
    {
        return Result.Try(() =>
        {
            var channelOptions = new GrpcChannelOptions
            {
                HttpHandler = handler
            };

            ApplyChannelRuntime(channelOptions, runtimeSnapshot);
            return GrpcChannel.ForAddress(profile.Address, channelOptions);
        });
    }

    private ValueTask<Result<System.Net.Security.SslClientAuthenticationOptions>> BuildSslOptionsAsync(GrpcControlPlaneClientProfile profile, CancellationToken cancellationToken)
    {
        return new(Task.Run(() =>
        {
            return Result.Try(() =>
            {
                var preferHttp3 = profile.Runtime?.EnableHttp3 ?? profile.PreferHttp3;
                var options = new System.Net.Security.SslClientAuthenticationOptions
                {
                    ApplicationProtocols = preferHttp3
                        ? new List<SslApplicationProtocol> { SslApplicationProtocol.Http3, SslApplicationProtocol.Http2 }
                        : new List<SslApplicationProtocol> { SslApplicationProtocol.Http2, SslApplicationProtocol.Http3 }
                };

                ApplyClientTls(options, profile);
                return options;
            });
        }, cancellationToken));
    }

    private static ValueTask<Result<GrpcRuntimeSnapshot>> BuildRuntimeSnapshotAsync(GrpcControlPlaneClientProfile profile, CancellationToken cancellationToken)
    {
        return new(Task.Run(() =>
        {
            var runtime = profile.Runtime;
            var snapshot = new GrpcRuntimeSnapshot(
                runtime?.MaxReceiveMessageSize,
                runtime?.MaxSendMessageSize,
                runtime?.KeepAlivePingDelay,
                runtime?.KeepAlivePingTimeout);
            return Ok(snapshot);
        }, cancellationToken));
    }

    private void ApplyClientTls(SslClientAuthenticationOptions sslOptions, GrpcControlPlaneClientProfile profile)
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
                ? X509RevocationMode.Online
                : X509RevocationMode.NoCheck;
        }
    }

    private static void ApplyHandlerRuntime(SocketsHttpHandler handler, GrpcRuntimeSnapshot runtimeSnapshot)
    {
        if (runtimeSnapshot.KeepAlivePingDelay is { } pingDelay)
        {
            handler.KeepAlivePingDelay = pingDelay;
        }

        if (runtimeSnapshot.KeepAlivePingTimeout is { } pingTimeout)
        {
            handler.KeepAlivePingTimeout = pingTimeout;
        }
    }

    private static void ApplyChannelRuntime(GrpcChannelOptions options, GrpcRuntimeSnapshot runtimeSnapshot)
    {
        if (runtimeSnapshot.MaxReceiveMessageSize is { } maxReceive)
        {
            options.MaxReceiveMessageSize = maxReceive;
        }

        if (runtimeSnapshot.MaxSendMessageSize is { } maxSend)
        {
            options.MaxSendMessageSize = maxSend;
        }
    }

    private readonly record struct GrpcRuntimeSnapshot(
        int? MaxReceiveMessageSize,
        int? MaxSendMessageSize,
        TimeSpan? KeepAlivePingDelay,
        TimeSpan? KeepAlivePingTimeout);
}
#pragma warning restore IDE0005
#pragma warning restore IDE0005
