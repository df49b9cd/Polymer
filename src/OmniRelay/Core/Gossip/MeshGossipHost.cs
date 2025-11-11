using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Peers;

namespace OmniRelay.Core.Gossip;

/// <summary>
/// Hosts the gossip listener (HTTP/3 + mTLS) and drives outbound gossip rounds.
/// </summary>
public sealed class MeshGossipHost : IMeshGossipAgent, IDisposable
{
    private readonly MeshGossipOptions _options;
    private readonly ILogger<MeshGossipHost> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly MeshGossipMembershipTable _membership;
    private readonly MeshGossipCertificateProvider _certificateProvider;
    private readonly IReadOnlyList<MeshGossipPeerEndpoint> _seedPeers;
    private readonly PeerLeaseHealthTracker? _leaseHealthTracker;
    private readonly ConcurrentDictionary<string, MeshGossipMemberStatus> _peerStatuses = new(StringComparer.Ordinal);
    private HttpClient? _httpClient;
    private WebApplication? _app;
    private Task? _serverTask;
    private CancellationTokenSource? _cts;
    private Task? _gossipLoop;
    private Task? _sweepLoop;
    private long _sequence;
    private bool _disposed;

    public MeshGossipHost(
        MeshGossipOptions options,
        MeshGossipMemberMetadata? metadata,
        ILogger<MeshGossipHost> logger,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null,
        PeerLeaseHealthTracker? leaseHealthTracker = null,
        MeshGossipCertificateProvider? certificateProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _leaseHealthTracker = leaseHealthTracker;
        _sequence = Stopwatch.GetTimestamp();

        ValidateOptions(options);

        var localMetadata = metadata ?? BuildMetadata(options);
        localMetadata = EnsureEndpoint(localMetadata, options);
        _membership = new MeshGossipMembershipTable(localMetadata.NodeId, localMetadata, _timeProvider);
        _certificateProvider = certificateProvider ?? new MeshGossipCertificateProvider(options, CreateCertificateLogger(loggerFactory));
        _seedPeers = options.GetNormalizedSeedPeers()
            .Select(value => MeshGossipPeerEndpoint.TryParse(value, out var endpoint) ? endpoint : (MeshGossipPeerEndpoint?)null)
            .Where(static endpoint => endpoint is not null)
            .Select(static endpoint => endpoint!.Value)
            .ToArray();
    }

    public bool IsEnabled => _options.Enabled;

    public MeshGossipMemberMetadata LocalMetadata => _membership.LocalMetadata;

    public MeshGossipClusterView Snapshot() => _membership.Snapshot();

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || _cts is not null)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        if (!_certificateProvider.IsConfigured)
        {
            throw new InvalidOperationException("mesh:gossip:tls:certificatePath must be configured when gossip is enabled.");
        }

        _httpClient = CreateHttpClient();
        _app = BuildListener();
        _serverTask = _app.RunAsync(token);
        _gossipLoop = RunGossipLoopAsync(token);
        _sweepLoop = RunSweepLoopAsync(token);

        _logger.LogInformation("Mesh gossip host listening on {Address}:{Port} (fanout={Fanout})", _options.BindAddress, _options.Port, _options.Fanout);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();

        try
        {
            if (_gossipLoop is not null)
            {
                await _gossipLoop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            if (_sweepLoop is not null)
            {
                await _sweepLoop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        _httpClient?.Dispose();
        _httpClient = null;
        _cts.Dispose();
        _cts = null;
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
            ActivityHeadersPropagator = null,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                RemoteCertificateValidationCallback = ValidateRemoteCertificate,
                CertificateRevocationCheckMode = _options.Tls.CheckCertificateRevocation
                    ? X509RevocationMode.Online
                    : X509RevocationMode.NoCheck,
                ClientCertificates = [],
                LocalCertificateSelectionCallback = (_, host, certificates, _, issuers) =>
                {
                    var cert = _certificateProvider.GetCertificate();
                    if (cert is not null && !certificates.Contains(cert))
                    {
                        certificates.Add(cert);
                    }

                    return cert;
                }
            }
        };

        var client = new HttpClient(handler)
        {
            Timeout = _options.PingTimeout,
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"omnirelay-mesh-gossip/{_options.MeshVersion}");
        return client;
    }

    private WebApplication BuildListener()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(_logger));
        builder.WebHost.UseKestrel(options =>
        {
            options.ConfigureEndpointDefaults(listenOptions => listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3);
            options.Listen(IPAddress.Parse(_options.BindAddress), _options.Port, listenOptions =>
            {
                listenOptions.UseHttps(CreateHttpsOptions());
            });
        });

        builder.Services.AddSingleton(this);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, MeshGossipJsonSerializerContext.Default);
        });

        var app = builder.Build();
        app.MapPost("/mesh/gossip/v1/messages", async (HttpContext context, MeshGossipEnvelope envelope, MeshGossipHost host) =>
        {
            var result = await host.ProcessEnvelopeAsync(envelope, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(result, MeshGossipJsonSerializerContext.Default.MeshGossipEnvelope);
        });

        return app;
    }

    private HttpsConnectionAdapterOptions CreateHttpsOptions()
    {
        return new HttpsConnectionAdapterOptions
        {
            SslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
            ClientCertificateMode = ClientCertificateMode.RequireCertificate,
            CheckCertificateRevocation = _options.Tls.CheckCertificateRevocation,
            ClientCertificateValidation = (certificate, chain, errors) =>
                ValidateRemoteCertificate(sender: null, certificate, chain, errors),
            ServerCertificateSelector = (_, _) => _certificateProvider.GetCertificate()
        };
    }

    private async Task<MeshGossipEnvelope> ProcessEnvelopeAsync(MeshGossipEnvelope envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (envelope is null || !string.Equals(envelope.SchemaVersion, MeshGossipOptions.CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            MeshGossipMetrics.RecordMessage("inbound", "failure");
            _logger.LogWarning("Rejected gossip envelope with incompatible schema {Schema}", envelope?.SchemaVersion);
            return BuildEnvelope();
        }

        _membership.MarkSender(envelope);
        foreach (var member in envelope.Members)
        {
            _membership.MarkObserved(member);
        }

        UpdateLeaseDiagnostics();
        var snapshot = _membership.Snapshot();
        RecordMetrics(snapshot);
        MeshGossipMetrics.RecordMessage("inbound", "success");
        return BuildEnvelope(snapshot);
    }

    private MeshGossipEnvelope BuildEnvelope(MeshGossipClusterView? snapshot = null)
    {
        snapshot ??= _membership.Snapshot();
        var members = snapshot.Members.Length > 32
            ? snapshot.Members.Take(32).ToImmutableArray()
            : snapshot.Members;

        var sender = snapshot.Members.FirstOrDefault(m => m.NodeId == LocalMetadata.NodeId)?.Metadata ?? LocalMetadata;

        return new MeshGossipEnvelope
        {
            SchemaVersion = MeshGossipOptions.CurrentSchemaVersion,
            Timestamp = _timeProvider.GetUtcNow(),
            Sequence = Interlocked.Increment(ref _sequence),
            Sender = sender,
            Members = members
        };
    }

    private async Task RunGossipLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.Interval, cancellationToken).ConfigureAwait(false);
                await ExecuteRoundAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mesh gossip round failed.");
            }
        }
    }

    private async Task ExecuteRoundAsync(CancellationToken cancellationToken)
    {
        if (_httpClient is null)
        {
            return;
        }

        var snapshot = _membership.Snapshot();
        var members = _membership.PickFanout(_options.Fanout);
        var targets = new List<MeshGossipPeerEndpoint>();

        foreach (var member in members)
        {
            if (MeshGossipPeerEndpoint.TryParse(member.Metadata.Endpoint ?? string.Empty, out var endpoint))
            {
                targets.Add(endpoint);
            }
        }

        if (targets.Count == 0 && _seedPeers.Count > 0)
        {
            targets.AddRange(_seedPeers.Take(_options.Fanout));
        }

        if (targets.Count == 0)
        {
            return;
        }

        foreach (var target in targets)
        {
            var envelope = BuildEnvelope(snapshot);
            using var request = new HttpRequestMessage(HttpMethod.Post, target.BuildRequestUri());
            request.Content = JsonContent.Create(envelope, MeshGossipJsonSerializerContext.Default.MeshGossipEnvelope);

            try
            {
                var start = Stopwatch.GetTimestamp();
                var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var responseEnvelope = await response.Content.ReadFromJsonAsync(MeshGossipJsonSerializerContext.Default.MeshGossipEnvelope, cancellationToken).ConfigureAwait(false);
                if (responseEnvelope is not null)
                {
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    _membership.MarkSender(responseEnvelope, elapsed);
                    foreach (var member in responseEnvelope.Members)
                    {
                        _membership.MarkObserved(member);
                    }

                    MeshGossipMetrics.RecordRoundTrip(responseEnvelope.Sender.NodeId, elapsed);
                }

                MeshGossipMetrics.RecordMessage("outbound", "success");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                MeshGossipMetrics.RecordMessage("outbound", "failure");
                _logger.LogDebug(ex, "Failed to gossip with {Target}", target);
            }
        }

        UpdateLeaseDiagnostics();
        RecordMetrics(_membership.Snapshot());
    }

    private async Task RunSweepLoopAsync(CancellationToken cancellationToken)
    {
        var suspicion = _options.SuspicionInterval;
        var leave = _options.SuspicionInterval + _options.PingTimeout * Math.Max(1, _options.RetransmitLimit);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.SuspicionInterval, cancellationToken).ConfigureAwait(false);
                _membership.Sweep(suspicion, leave);
                RecordMetrics(_membership.Snapshot());
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mesh gossip sweep failed.");
            }
        }
    }

    private void RecordMetrics(MeshGossipClusterView snapshot)
    {
        var alive = snapshot.Members.Count(m => m.Status == MeshGossipMemberStatus.Alive);
        var suspect = snapshot.Members.Count(m => m.Status == MeshGossipMemberStatus.Suspect);
        var left = snapshot.Members.Count(m => m.Status == MeshGossipMemberStatus.Left);
        MeshGossipMetrics.RecordMemberCounts(alive, suspect, left);
        TrackPeerStatuses(snapshot);
    }

    private void UpdateLeaseDiagnostics()
    {
        if (_leaseHealthTracker is null)
        {
            return;
        }

        var snapshot = _membership.Snapshot();
        foreach (var member in snapshot.Members)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mesh.role"] = member.Metadata.Role,
                ["mesh.cluster"] = member.Metadata.ClusterId,
                ["mesh.region"] = member.Metadata.Region,
                ["mesh.version"] = member.Metadata.MeshVersion,
                ["mesh.http3"] = member.Metadata.Http3Support ? "true" : "false"
            };

            foreach (var label in member.Metadata.Labels)
            {
                metadata[$"label.{label.Key}"] = label.Value;
            }

            _leaseHealthTracker.RecordGossip(member.NodeId, metadata);
        }
    }

    private void TrackPeerStatuses(MeshGossipClusterView snapshot)
    {
        foreach (var member in snapshot.Members)
        {
            if (string.Equals(member.NodeId, LocalMetadata.NodeId, StringComparison.Ordinal))
            {
                continue;
            }

            var newStatus = member.Status;
            MeshGossipMemberStatus? previousStatus = null;

            if (_peerStatuses.TryGetValue(member.NodeId, out var previous))
            {
                if (previous == newStatus)
                {
                    continue;
                }

                previousStatus = previous;
            }

            _peerStatuses[member.NodeId] = newStatus;
            LogPeerStatusChange(member, previousStatus);
        }
    }

    private void LogPeerStatusChange(MeshGossipMemberSnapshot member, MeshGossipMemberStatus? previousStatus)
    {
        var metadata = member.Metadata;
        switch (member.Status)
        {
            case MeshGossipMemberStatus.Alive:
                if (previousStatus is null || previousStatus == MeshGossipMemberStatus.Left)
                {
                    _logger.LogInformation(
                        "Mesh peer {PeerId} joined cluster {ClusterId} as {Role} (region {Region}, version {Version}, http3={Http3}).",
                        member.NodeId,
                        metadata.ClusterId,
                        metadata.Role,
                        metadata.Region,
                        metadata.MeshVersion,
                        metadata.Http3Support);
                }
                else
                {
                    _logger.LogInformation(
                        "Mesh peer {PeerId} recovered from {PreviousStatus} (cluster {ClusterId}, role {Role}).",
                        member.NodeId,
                        previousStatus,
                        metadata.ClusterId,
                        metadata.Role);
                }
                break;
            case MeshGossipMemberStatus.Suspect:
                _logger.LogWarning(
                    "Mesh peer {PeerId} marked suspect (cluster {ClusterId}, role {Role}, lastSeen={LastSeen}).",
                    member.NodeId,
                    metadata.ClusterId,
                    metadata.Role,
                    member.LastSeen);
                break;
            case MeshGossipMemberStatus.Left:
                _logger.LogWarning(
                    "Mesh peer {PeerId} left gossip cluster {ClusterId} (role {Role}).",
                    member.NodeId,
                    metadata.ClusterId,
                    metadata.Role);
                _leaseHealthTracker?.RecordDisconnect(member.NodeId, "gossip-left");
                break;
        }
    }

    private bool ValidateRemoteCertificate(object? sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (_options.Tls.AllowUntrustedCertificates)
        {
            return true;
        }

        if (certificate is null)
        {
            return false;
        }

        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        var thumbprint = certificate.GetCertHashString()?.ToUpperInvariant();
        if (_options.Tls.AllowedThumbprints.Count > 0)
        {
            return _options.Tls.AllowedThumbprints.Contains(thumbprint);
        }

        if (chain is not null)
        {
            chain.ChainPolicy.RevocationMode = _options.Tls.CheckCertificateRevocation
                ? X509RevocationMode.Online
                : X509RevocationMode.NoCheck;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EndCertificateOnly;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            if (chain.Build((X509Certificate2)certificate))
            {
                return true;
            }
        }

        return errors == SslPolicyErrors.None;
    }

    private static MeshGossipMemberMetadata BuildMetadata(MeshGossipOptions options)
    {
        return new MeshGossipMemberMetadata
        {
            NodeId = options.NodeId,
            Role = options.Role,
            ClusterId = options.ClusterId,
            Region = options.Region,
            MeshVersion = options.MeshVersion,
            Http3Support = options.Http3Support,
            MetadataVersion = 1,
            Labels = options.Labels.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static MeshGossipMemberMetadata EnsureEndpoint(MeshGossipMemberMetadata metadata, MeshGossipOptions options)
    {
        var host = string.IsNullOrWhiteSpace(options.AdvertiseHost)
            ? Dns.GetHostName()
            : options.AdvertiseHost!;
        var port = options.AdvertisePort ?? options.Port;
        return metadata with { Endpoint = $"{host}:{port}" };
    }

    private static void ValidateOptions(MeshGossipOptions options)
    {
        if (options.Port <= 0 || options.Port > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Port), "mesh:gossip:port must be between 1 and 65535.");
        }

        if (options.Fanout <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Fanout), "mesh:gossip:fanout must be greater than zero.");
        }

        if (options.Interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Interval), "mesh:gossip:interval must be positive.");
        }
    }

    private static ILogger<MeshGossipCertificateProvider> CreateCertificateLogger(ILoggerFactory factory) =>
        factory.CreateLogger<MeshGossipCertificateProvider>();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mesh gossip host stop threw during dispose.");
        }

        _certificateProvider.Dispose();
    }

    private sealed class ForwardingLoggerProvider : ILoggerProvider
    {
        private readonly ILogger _logger;

        public ForwardingLoggerProvider(ILogger logger) => _logger = logger;

        public ILogger CreateLogger(string categoryName) => new ForwardingLogger(_logger);

        public void Dispose()
        {
        }

        private sealed class ForwardingLogger : ILogger
        {
            private readonly ILogger _inner;

            public ForwardingLogger(ILogger inner) => _inner = inner;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                _inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
