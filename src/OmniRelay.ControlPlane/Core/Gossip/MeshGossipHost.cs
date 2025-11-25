using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using Hugo;
using Hugo.Policies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniRelay.ControlPlane.Primitives;
using OmniRelay.Identity;
using OmniRelay.Diagnostics;
using OmniRelay.Security.Secrets;
using static Hugo.Go;

namespace OmniRelay.Core.Gossip;

/// <summary>
/// Hosts the gossip listener (HTTP/3 + mTLS) and drives outbound gossip rounds.
/// </summary>
public sealed partial class MeshGossipHost : IMeshGossipAgent, IDisposable
{
    private readonly MeshGossipOptions _options;
    private readonly ILogger<MeshGossipHost> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly MeshGossipMembershipTable _membership;
    private readonly TransportTlsManager _tlsManager;
    private readonly List<MeshGossipPeerEndpoint> _seedPeers;
    private readonly PeerLeaseHealthTracker? _leaseHealthTracker;
    private readonly MeshGossipPeerView _peerView = new();
    private readonly ResultExecutionPolicy _gossipSendPolicy = ResultExecutionPolicy.None.WithRetry(
        ResultRetryPolicy.Exponential(
            maxAttempts: 3,
            TimeSpan.FromMilliseconds(50),
            2.0,
            TimeSpan.FromMilliseconds(500)));
    private readonly ConcurrentDictionary<string, MeshGossipMemberStatus> _peerStatuses = new(StringComparer.Ordinal);
    private HttpClient? _httpClient;
    private WebApplication? _app;
    private Task? _serverTask;
    private CancellationTokenSource? _cts;
    private TaskQueue<Func<CancellationToken, ValueTask<Result<Unit>>>>? _sendQueue;
    private TaskQueueOptions? _sendQueueOptions;
    private Task? _gossipLoop;
    private Task? _sweepLoop;
    private Task? _shuffleLoop;
    private SafeTaskQueueWrapper<Func<CancellationToken, ValueTask<Result<Unit>>>>? _sendSafeQueue;
    private TaskQueueChannelAdapter<Func<CancellationToken, ValueTask<Result<Unit>>>>? _sendAdapter;
    private Task? _sendPump;
    private long _sequence;
    private bool _disposed;
    private static readonly Action<ILogger, string, int, int, Exception?> GossipListeningLog =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Information,
            new EventId(1, "MeshGossipListening"),
            "Mesh gossip host listening on {Address}:{Port} (fanout={Fanout})");
    private static readonly Action<ILogger, string?, Exception?> GossipSchemaRejectedLog =
        LoggerMessage.Define<string?>(
            LogLevel.Warning,
            new EventId(2, "MeshGossipSchemaRejected"),
            "Rejected gossip envelope with incompatible schema {Schema}");

    public MeshGossipHost(
        MeshGossipOptions options,
        MeshGossipMemberMetadata? metadata,
        ILogger<MeshGossipHost> logger,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null,
        PeerLeaseHealthTracker? leaseHealthTracker = null,
        TransportTlsManager? tlsManager = null,
        ISecretProvider? secretProvider = null)
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
        _tlsManager = tlsManager ?? new TransportTlsManager(
            options.Tls.ToTransportTlsOptions(options.CertificateReloadInterval),
            CreateCertificateLogger(loggerFactory),
            secretProvider);
        _seedPeers = [.. options.GetNormalizedSeedPeers()
            .Select(value => MeshGossipPeerEndpoint.TryParse(value, out var endpoint) ? endpoint : (MeshGossipPeerEndpoint?)null)
            .Where(static endpoint => endpoint is not null)
            .Select(static endpoint => endpoint!.Value)];
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

        if (!_tlsManager.IsConfigured)
        {
            throw new InvalidOperationException("mesh:gossip:tls:certificatePath must be configured when gossip is enabled.");
        }

        _httpClient = CreateHttpClient();
        _sendQueueOptions = CreateSendQueueOptions();
        _sendQueue = new TaskQueue<Func<CancellationToken, ValueTask<Result<Unit>>>>(_sendQueueOptions, _timeProvider, (_, _) => ValueTask.CompletedTask);
        _sendSafeQueue = new SafeTaskQueueWrapper<Func<CancellationToken, ValueTask<Result<Unit>>>>(_sendQueue, ownsQueue: true);
        _sendAdapter = TaskQueueChannelAdapter<Func<CancellationToken, ValueTask<Result<Unit>>>>.Create(
            _sendQueue,
            concurrency: Math.Max(1, _options.MaxOutboundPerRound),
            ownsQueue: false);
        _sendPump = RunSendPumpAsync(token);

        _app = BuildListener();
        _serverTask = _app.RunAsync(token);
        _gossipLoop = RunGossipLoopAsync(token);
        _sweepLoop = RunSweepLoopAsync(token);
        _shuffleLoop = RunShuffleLoopAsync(token);

        GossipListeningLog(_logger, _options.BindAddress, _options.Port, _options.Fanout, null);
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
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Expected when CTS is cancelled during shutdown
        }

        try
        {
            if (_sweepLoop is not null)
            {
                await _sweepLoop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Expected when CTS is cancelled during shutdown
        }
        try
        {
            if (_shuffleLoop is not null)
            {
                await _shuffleLoop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Expected when CTS is cancelled during shutdown
        }

        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        _httpClient?.Dispose();
        _httpClient = null;
        if (_sendPump is not null)
        {
            try
            {
                await _sendPump.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // Expected when cancellation originates from StopAsync.
            }

            _sendPump = null;
        }

        if (_sendAdapter is not null)
        {
            await _sendAdapter.DisposeAsync().ConfigureAwait(false);
            _sendAdapter = null;
        }

        if (_sendSafeQueue is not null)
        {
            await _sendSafeQueue.DisposeAsync().ConfigureAwait(false);
            _sendSafeQueue = null;
        }

        _sendQueue = null;
        _sendQueueOptions = null;
        _cts.Dispose();
        _cts = null;
    }

    private async Task RunSendPumpAsync(CancellationToken cancellationToken)
    {
        if (_sendAdapter is null || _sendSafeQueue is null)
        {
            return;
        }

        var maxDeliveryAttempts = _sendQueueOptions?.MaxDeliveryAttempts ?? 0;

        const int BatchSize = 16;
        var flushInterval = TimeSpan.FromMilliseconds(25);

        var windowsResult = await Result.RetryWithPolicyAsync<ChannelReader<IReadOnlyList<TaskQueueLease<Func<CancellationToken, ValueTask<Result<Unit>>>>>>>(
            async (ctx, ct) =>
            {
                var reader = await ResultPipelineChannels.WindowAsync(
                    ctx,
                    _sendAdapter.Reader,
                    BatchSize,
                    flushInterval,
                    ct).ConfigureAwait(false);
                return Ok(reader);
            },
            ResultExecutionPolicy.None,
            _timeProvider,
            cancellationToken).ConfigureAwait(false);

        if (windowsResult.IsFailure)
        {
            MeshGossipHostLog.GossipRoundFailed(_logger, windowsResult.Error?.Cause ?? new InvalidOperationException(windowsResult.Error?.Message ?? "gossip send window failed"));
            return;
        }

        var windows = windowsResult.Value;

        await foreach (var batch in windows.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var lease in batch)
            {
                var safeLease = _sendSafeQueue.Wrap(lease);
                Result<Unit> result;
                try
                {
                    result = await lease.Value(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken || cancellationToken.IsCancellationRequested)
                {
                    result = Err<Unit>(Error.Canceled("gossip send canceled", oce.CancellationToken));
                }
                catch (Exception ex)
                {
                    result = Err<Unit>(Error.FromException(ex));
                }

                if (result.IsSuccess)
                {
                    var complete = await safeLease.CompleteAsync(cancellationToken).ConfigureAwait(false);
                    if (complete.IsFailure)
                    {
                        MeshGossipHostLog.GossipRoundFailed(_logger, complete.Error?.Cause ?? new InvalidOperationException(complete.Error?.Message ?? "gossip send completion failed"));
                        return;
                    }

                    continue;
                }

                var requeue = lease.Attempt < maxDeliveryAttempts && result.Error?.Code != ErrorCodes.Canceled;
                var failed = await safeLease.FailAsync(result.Error!, requeue, cancellationToken).ConfigureAwait(false);
                if (failed.IsFailure)
                {
                    MeshGossipHostLog.GossipRoundFailed(_logger, failed.Error?.Cause ?? new InvalidOperationException(failed.Error?.Message ?? "gossip send failure handling failed"));
                    return;
                }

                if (result.IsFailure)
                {
                    MeshGossipHostLog.GossipRoundFailed(_logger, result.Error?.Cause ?? new InvalidOperationException(result.Error?.Message ?? "gossip send failed"));
                    return;
                }
            }
        }
    }

    private TaskQueueOptions CreateSendQueueOptions()
    {
        return new TaskQueueOptions
        {
            Capacity = Math.Max(128, _options.MaxOutboundPerRound * 4),
            LeaseDuration = _options.PingTimeout,
            HeartbeatInterval = TimeSpan.FromMilliseconds(Math.Max(200, _options.PingTimeout.TotalMilliseconds / 2)),
            LeaseSweepInterval = TimeSpan.FromSeconds(5),
            RequeueDelay = TimeSpan.FromMilliseconds(100),
            MaxDeliveryAttempts = 5,
            Name = "mesh-gossip-send"
        };
    }

    private Func<CancellationToken, ValueTask<Result<Unit>>> CreateGossipSendWork(MeshGossipPeerEndpoint target, MeshGossipEnvelope envelope)
    {
        return async ct =>
        {
            try
            {
                if (_httpClient is null)
                {
                    return Err<Unit>(Error.From("http client not initialized", "gossip.http.missing"));
                }

                var start = Stopwatch.GetTimestamp();
                using var request = new HttpRequestMessage(HttpMethod.Post, target.BuildRequestUri());
                request.Content = JsonContent.Create(envelope, MeshGossipJsonSerializerContext.Default.MeshGossipEnvelope);

                var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var responseEnvelope = await response.Content.ReadFromJsonAsync(
                    MeshGossipJsonSerializerContext.Default.MeshGossipEnvelope,
                    ct).ConfigureAwait(false);

                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (responseEnvelope is not null)
                {
                    _membership.MarkSender(responseEnvelope, elapsedMs);
                    foreach (var member in responseEnvelope.Members)
                    {
                        _membership.MarkObserved(member);
                    }

                    MeshGossipMetrics.RecordRoundTrip(responseEnvelope.Sender.NodeId, elapsedMs);
                }

                MeshGossipMetrics.RecordMessage("outbound", "success");
                return Ok(Unit.Value);
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == ct)
            {
                return Err<Unit>(Error.Canceled("gossip send canceled", ct));
            }
            catch (HttpRequestException ex)
            {
                MeshGossipMetrics.RecordMessage("outbound", "failure");
                MeshGossipHostLog.GossipRequestFailed(_logger, target.ToString(), ex);
                return Err<Unit>(Error.FromException(ex));
            }
            catch (JsonException ex)
            {
                MeshGossipMetrics.RecordMessage("outbound", "failure");
                MeshGossipHostLog.GossipRequestFailed(_logger, target.ToString(), ex);
                return Err<Unit>(Error.FromException(ex));
            }
            catch (Exception ex)
            {
                MeshGossipMetrics.RecordMessage("outbound", "failure");
                return Err<Unit>(Error.FromException(ex));
            }
        };
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
                    var cert = _tlsManager.GetCertificate();
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
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/mesh/gossip/v1/messages" && HttpMethods.IsPost(context.Request.Method))
            {
                var envelope = await context.Request.ReadFromJsonAsync(
                        MeshGossipJsonSerializerContext.Default.MeshGossipEnvelope,
                        context.RequestAborted)
                    .ConfigureAwait(false);

                if (envelope is null)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("{\"error\":\"Request body required.\"}", context.RequestAborted)
                        .ConfigureAwait(false);
                    return;
                }

                var result = await ProcessEnvelopeAsync(envelope, context.RequestAborted).ConfigureAwait(false);

                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(
                        context.Response.Body,
                        result,
                        MeshGossipJsonSerializerContext.Default.MeshGossipEnvelope,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            }

            if (context.Request.Path == "/mesh/gossip/v1/shuffle" && HttpMethods.IsPost(context.Request.Method))
            {
                var request = await context.Request.ReadFromJsonAsync(
                        MeshGossipJsonSerializerContext.Default.MeshGossipShuffleEnvelope,
                        context.RequestAborted)
                    .ConfigureAwait(false);

                if (request is null)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("{\"error\":\"Request body required.\"}", context.RequestAborted)
                        .ConfigureAwait(false);
                    return;
                }

                var response = HandleShuffle(request);

                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(
                        context.Response.Body,
                        response,
                        MeshGossipJsonSerializerContext.Default.MeshGossipShuffleEnvelope,
                        context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            }

            await next().ConfigureAwait(false);
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
            ServerCertificateSelector = (_, _) => _tlsManager.GetCertificate()
        };
    }

    private async Task<MeshGossipEnvelope> ProcessEnvelopeAsync(MeshGossipEnvelope envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (envelope is null || !string.Equals(envelope.SchemaVersion, MeshGossipOptions.CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            MeshGossipMetrics.RecordMessage("inbound", "failure");
            GossipSchemaRejectedLog(_logger, envelope?.SchemaVersion, null);
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
        PublishMembershipEvent("inbound-envelope");
        return BuildEnvelope(snapshot);
    }

    private MeshGossipEnvelope BuildEnvelope(MeshGossipClusterView? snapshot = null)
    {
        snapshot ??= _membership.Snapshot();
        var members = snapshot.Members.Length > 32
            ? [.. snapshot.Members.Take(32)]
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
                var delayResult = await AsyncDelay.DelayAsync(_options.Interval, _timeProvider, cancellationToken).ConfigureAwait(false);
                if (delayResult.IsFailure)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    MeshGossipHostLog.GossipRoundFailed(_logger, delayResult.Error?.Cause ?? new InvalidOperationException(delayResult.Error?.Message ?? "delay failed"));
                    continue;
                }

                var round = await Result.RetryWithPolicyAsync<Unit>(
                    async (_, ct) =>
                    {
                        await ExecuteRoundAsync(ct).ConfigureAwait(false);
                        return Ok(Unit.Value);
                    },
                    _gossipSendPolicy,
                    _timeProvider,
                    cancellationToken).ConfigureAwait(false);

                if (round.IsFailure)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    MeshGossipHostLog.GossipRoundFailed(_logger, round.Error?.Cause ?? new InvalidOperationException(round.Error?.Message ?? "gossip round failed"));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Do not catch general exception types
            // Generic catch is intentional to prevent gossip loop from crashing.
            // This is a background service that should be resilient to unexpected failures.
            catch (Exception ex)
            {
                MeshGossipHostLog.GossipRoundFailed(_logger, ex);
            }
#pragma warning restore CA1031
        }
    }

    private async Task ExecuteRoundAsync(CancellationToken cancellationToken)
    {
        if (_httpClient is null)
        {
            return;
        }

        var snapshot = _membership.Snapshot();
        var alivePeers = snapshot.Members
            .Where(member => member.Status == MeshGossipMemberStatus.Alive && !string.Equals(member.NodeId, snapshot.LocalNodeId, StringComparison.Ordinal))
            .Select(member => ParseEndpoint(member.Metadata.Endpoint))
            .OfType<MeshGossipPeerEndpoint>();

        _peerView.Update(alivePeers, _seedPeers, _options.ActiveViewSize, _options.PassiveViewSize);
        var viewSizes = _peerView.SnapshotCounts();
        MeshGossipMetrics.RecordViewSizes(viewSizes.Active, viewSizes.Passive);

        var dynamicFanout = ComputeFanout(snapshot, _options);
        var targets = _peerView.SelectTargets(dynamicFanout, _options.MaxOutboundPerRound);

        if (targets.Count == 0)
        {
            return;
        }

        var distinctTargets = targets.Distinct().Count();
        var duplicates = targets.Count - distinctTargets;
        MeshGossipMetrics.RecordFanout(dynamicFanout, targets.Count, duplicates);

        foreach (var target in targets)
        {
            var envelope = BuildEnvelope(snapshot);
            var work = CreateGossipSendWork(target, envelope);

            if (_sendSafeQueue is not null)
            {
                var enqueueResult = await _sendSafeQueue.EnqueueAsync(work, cancellationToken).ConfigureAwait(false);
                if (enqueueResult.IsFailure)
                {
                    MeshGossipHostLog.GossipRoundFailed(_logger, enqueueResult.Error?.Cause ?? new InvalidOperationException(enqueueResult.Error?.Message ?? "gossip enqueue failed"));
                }
            }
            else
            {
                var sendResult = await work(cancellationToken).ConfigureAwait(false);
                if (sendResult.IsFailure)
                {
                    MeshGossipHostLog.GossipRoundFailed(_logger, sendResult.Error?.Cause ?? new InvalidOperationException(sendResult.Error?.Message ?? "gossip send failed"));
                }
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
                var delayResult = await AsyncDelay.DelayAsync(_options.SuspicionInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                if (delayResult.IsFailure)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    MeshGossipHostLog.GossipRoundFailed(_logger, delayResult.Error?.Cause ?? new InvalidOperationException(delayResult.Error?.Message ?? "delay failed"));
                    continue;
                }
                _membership.Sweep(suspicion, leave);
                RecordMetrics(_membership.Snapshot());
                PublishMembershipEvent("sweep");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Do not catch general exception types
            // Generic catch is intentional to prevent sweep loop from crashing.
            // This is a background service that should be resilient to unexpected failures.
            catch (Exception ex)
            {
                MeshGossipHostLog.GossipSweepFailed(_logger, ex);
            }
#pragma warning restore CA1031
        }
    }

    private async Task RunShuffleLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var delayResult = await AsyncDelay.DelayAsync(_options.ShuffleInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                if (delayResult.IsFailure)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    MeshGossipHostLog.GossipRoundFailed(_logger, delayResult.Error?.Cause ?? new InvalidOperationException(delayResult.Error?.Message ?? "delay failed"));
                    continue;
                }

                var target = _peerView.SelectShuffleTarget();
                if (target is null || _httpClient is null)
                {
                    continue;
                }

                var payload = _peerView.Sample(_options.ShuffleSampleSize);
                if (payload.Count == 0)
                {
                    continue;
                }

                var envelope = new MeshGossipShuffleEnvelope(GetLocalEndpoint(), payload.Select(static p => p.ToString()).ToArray());

                var sendResult = await Result.RetryWithPolicyAsync<MeshGossipShuffleEnvelope?>(
                    async (_, ct) =>
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, target.Value.BuildShuffleUri());
                        request.Content = JsonContent.Create(envelope, MeshGossipJsonSerializerContext.Default.MeshGossipShuffleEnvelope);

                        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();

                        var payloadResponse = await response.Content.ReadFromJsonAsync(
                            MeshGossipJsonSerializerContext.Default.MeshGossipShuffleEnvelope,
                            ct).ConfigureAwait(false);

                        return Ok(payloadResponse);
                    },
                    _gossipSendPolicy,
                    _timeProvider,
                    cancellationToken).ConfigureAwait(false);

                if (sendResult.IsFailure)
                {
                    MeshGossipHostLog.GossipRoundFailed(_logger, sendResult.Error?.Cause ?? new InvalidOperationException(sendResult.Error?.Message ?? "shuffle send failed"));
                    continue;
                }

                var responseEnvelope = sendResult.Value;

                if (responseEnvelope is not null)
                {
                    var inbound = responseEnvelope.Endpoints
                        .Select(endpoint => MeshGossipPeerEndpoint.TryParse(endpoint, out var parsed) ? parsed : (MeshGossipPeerEndpoint?)null)
                        .Where(static endpoint => endpoint is not null)
                        .Select(static endpoint => endpoint!.Value);
                    _peerView.MergeShuffle(inbound, _options.PassiveViewSize);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Shuffle is best-effort; continue on errors.
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
                    MeshGossipHostLog.PeerJoined(
                        _logger,
                        member.NodeId,
                        metadata.ClusterId ?? string.Empty,
                        metadata.Role ?? string.Empty,
                        metadata.Region ?? string.Empty,
                        metadata.MeshVersion ?? string.Empty,
                        metadata.Http3Support);
                }
                else
                {
                    MeshGossipHostLog.PeerRecovered(
                        _logger,
                        member.NodeId,
                        previousStatus?.ToString() ?? "unknown",
                        metadata.ClusterId ?? string.Empty,
                        metadata.Role ?? string.Empty);
                }
                break;
            case MeshGossipMemberStatus.Suspect:
                MeshGossipHostLog.PeerSuspect(
                    _logger,
                    member.NodeId,
                    metadata.ClusterId ?? string.Empty,
                    metadata.Role ?? string.Empty,
                    member.LastSeen ?? DateTimeOffset.MinValue);
                break;
            case MeshGossipMemberStatus.Left:
                MeshGossipHostLog.PeerLeft(
                    _logger,
                    member.NodeId,
                    metadata.ClusterId ?? string.Empty,
                    metadata.Role ?? string.Empty);
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
            return thumbprint is not null && _options.Tls.AllowedThumbprints.Contains(thumbprint);
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

    private MeshGossipShuffleEnvelope HandleShuffle(MeshGossipShuffleEnvelope request)
    {
        var inbound = request.Endpoints
            .Select(endpoint => MeshGossipPeerEndpoint.TryParse(endpoint, out var parsed) ? parsed : (MeshGossipPeerEndpoint?)null)
            .Where(static endpoint => endpoint is not null)
            .Select(static endpoint => endpoint!.Value);

        _peerView.MergeShuffle(inbound, _options.PassiveViewSize);

        var sample = _peerView.Sample(_options.ShuffleSampleSize);
        var responder = GetLocalEndpoint();

        return new MeshGossipShuffleEnvelope(responder, sample.Select(static p => p.ToString()).ToArray());
    }

    private static int ComputeFanout(MeshGossipClusterView snapshot, MeshGossipOptions options)
    {
        if (!options.AdaptiveFanout)
        {
            return Math.Max(1, options.Fanout);
        }

        var size = Math.Max(2, snapshot.Members.Length);
        var baseFanout = (int)Math.Ceiling(options.FanoutCoefficient * Math.Log(size, 2));
        baseFanout = Math.Clamp(baseFanout, Math.Max(1, options.FanoutFloor), Math.Max(options.FanoutFloor, options.FanoutCeiling));

        var suspect = snapshot.Members.Count(m => m.Status == MeshGossipMemberStatus.Suspect);
        var left = snapshot.Members.Count(m => m.Status == MeshGossipMemberStatus.Left);
        var total = Math.Max(1, snapshot.Members.Length);
        var failureRate = (double)(suspect + left) / total;

        var boosted = (int)Math.Ceiling(baseFanout * (1.0 + failureRate));
        boosted = Math.Min(boosted, options.FanoutCeiling);
        boosted = Math.Min(boosted, options.ActiveViewSize);

        return Math.Max(1, boosted);
    }

    private static void ValidateOptions(MeshGossipOptions options)
    {
        if (options.Port <= 0 || options.Port > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "mesh:gossip:port must be between 1 and 65535.");
        }

        if (options.Fanout <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "mesh:gossip:fanout must be greater than zero.");
        }

        if (options.Interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "mesh:gossip:interval must be positive.");
        }

        if (options.ActiveViewSize <= 0 || options.PassiveViewSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "mesh:gossip:view sizes must be greater than zero.");
        }

        if (options.MaxOutboundPerRound <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "mesh:gossip:maxOutboundPerRound must be greater than zero.");
        }

        if (options.ShuffleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "mesh:gossip:shuffleInterval must be positive.");
        }

        if (options.ShuffleSampleSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "mesh:gossip:shuffleSampleSize must be greater than zero.");
        }
    }

    private sealed class MeshGossipPeerView
    {
        private readonly object _lock = new();
        private readonly List<MeshGossipPeerEndpoint> _active = new();
        private readonly List<MeshGossipPeerEndpoint> _passive = new();

        public void Update(IEnumerable<MeshGossipPeerEndpoint> alive, IReadOnlyList<MeshGossipPeerEndpoint> seeds, int activeSize, int passiveSize)
        {
            lock (_lock)
            {
                var aliveSet = new HashSet<MeshGossipPeerEndpoint>(alive);

                // Drop peers that disappeared
                _active.RemoveAll(endpoint => !aliveSet.Contains(endpoint));
                _passive.RemoveAll(endpoint => !aliveSet.Contains(endpoint) && !seeds.Contains(endpoint));

                // Add new peers into passive view first
                foreach (var endpoint in aliveSet)
                {
                    if (!_active.Contains(endpoint) && !_passive.Contains(endpoint))
                    {
                        _passive.Add(endpoint);
                    }
                }

                // Ensure seeds are present at least in passive
                foreach (var seed in seeds)
                {
                    if (!_active.Contains(seed) && !_passive.Contains(seed))
                    {
                        _passive.Add(seed);
                    }
                }

                // Move random peers from passive into active until target size reached
                PromotePassiveToActive(activeSize);

                // Trim active/passive to configured limits
                TrimRandom(_active, activeSize);
                TrimRandom(_passive, passiveSize);
            }
        }

        public List<MeshGossipPeerEndpoint> SelectTargets(int fanout, int maxOutbound)
        {
            var limit = Math.Max(1, Math.Min(fanout, maxOutbound > 0 ? maxOutbound : fanout));

            lock (_lock)
            {
                if (_active.Count == 0 && _passive.Count > 0)
                {
                    PromotePassiveToActive(limit);
                }

                var targets = new List<MeshGossipPeerEndpoint>(Math.Min(limit, _active.Count + _passive.Count));
                AddRandom(_active, targets, limit);
                if (targets.Count < limit)
                {
                    AddRandom(_passive, targets, limit - targets.Count);
                }

                return targets;
            }
        }

        public MeshGossipPeerEndpoint? SelectShuffleTarget()
        {
            lock (_lock)
            {
                if (_active.Count == 0 && _passive.Count == 0)
                {
                    return null;
                }

                if (_active.Count == 0)
                {
                    var idx = Random.Shared.Next(_passive.Count);
                    return _passive[idx];
                }

                var selected = Random.Shared.Next(_active.Count);
                return _active[selected];
            }
        }

        public List<MeshGossipPeerEndpoint> Sample(int count)
        {
            lock (_lock)
            {
                var sample = new List<MeshGossipPeerEndpoint>(Math.Min(count, _active.Count + _passive.Count));
                AddRandom(_active, sample, count);
                if (sample.Count < count)
                {
                    AddRandom(_passive, sample, count - sample.Count);
                }

                return sample;
            }
        }

        public void MergeShuffle(IEnumerable<MeshGossipPeerEndpoint> inbound, int passiveSize)
        {
            lock (_lock)
            {
                foreach (var endpoint in inbound)
                {
                    if (_active.Contains(endpoint) || _passive.Contains(endpoint))
                    {
                        continue;
                    }

                    _passive.Add(endpoint);
                }

                TrimRandom(_passive, passiveSize);
            }
        }

        public (int Active, int Passive) SnapshotCounts()
        {
            lock (_lock)
            {
                return (_active.Count, _passive.Count);
            }
        }

        private void PromotePassiveToActive(int activeSize)
        {
            var needed = Math.Max(0, activeSize - _active.Count);
            if (needed == 0 || _passive.Count == 0)
            {
                return;
            }

            AddRandom(_passive, _active, needed, removeFromSource: true);
        }

        private static void TrimRandom(List<MeshGossipPeerEndpoint> list, int maxSize)
        {
            if (list.Count <= maxSize)
            {
                return;
            }

            var keep = Math.Max(0, maxSize);
            var count = list.Count;

            for (var i = 0; i < keep; i++)
            {
                var j = Random.Shared.Next(i, count);
                (list[i], list[j]) = (list[j], list[i]);
            }

            list.RemoveRange(keep, count - keep);
        }

        private static void AddRandom(List<MeshGossipPeerEndpoint> source, List<MeshGossipPeerEndpoint> destination, int count, bool removeFromSource = false)
        {
            if (count <= 0 || source.Count == 0)
            {
                return;
            }

            var take = Math.Min(count, source.Count);
            for (var i = 0; i < take; i++)
            {
                var j = Random.Shared.Next(i, source.Count);
                (source[i], source[j]) = (source[j], source[i]);
                destination.Add(source[i]);
            }

            if (removeFromSource)
            {
                source.RemoveRange(0, take);
            }
        }
    }

    private static MeshGossipPeerEndpoint? ParseEndpoint(string? endpoint)
    {
        return MeshGossipPeerEndpoint.TryParse(endpoint ?? string.Empty, out var parsed)
            ? parsed
            : null;
    }

    private string GetLocalEndpoint()
    {
        var local = _membership.LocalMetadata.Endpoint;
        if (!string.IsNullOrWhiteSpace(local))
        {
            return local!;
        }

        var host = string.IsNullOrWhiteSpace(_options.AdvertiseHost) ? Dns.GetHostName() : _options.AdvertiseHost!;
        var port = _options.AdvertisePort ?? _options.Port;
        return $"{host}:{port}";
    }

    private static void PublishMembershipEvent(string reason, MeshGossipMemberSnapshot? changedMember = null)
    {
        _ = reason;
        _ = changedMember;
        // Control-plane event bus publishing removed in data-plane split.
    }

    private static ILogger<TransportTlsManager> CreateCertificateLogger(ILoggerFactory factory) =>
        factory.CreateLogger<TransportTlsManager>();

    internal void ForcePeerStatus(string nodeId, MeshGossipMemberStatus status)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new ArgumentException("Node id must be provided.", nameof(nodeId));
        }

        var snapshot = _membership.Snapshot();
        var metadata = snapshot.Members
            .FirstOrDefault(member => string.Equals(member.NodeId, nodeId, StringComparison.Ordinal))
            ?.Metadata ?? new MeshGossipMemberMetadata { NodeId = nodeId };

        var forcedSnapshot = new MeshGossipMemberSnapshot
        {
            NodeId = nodeId,
            Status = status,
            LastSeen = _timeProvider.GetUtcNow(),
            Metadata = metadata
        };

        _membership.MarkObserved(forcedSnapshot);
        PublishMembershipEvent("forced-status", forcedSnapshot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            StopAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            MeshGossipHostLog.DisposalFailed(_logger, ex);
        }

        _tlsManager.Dispose();
    }

    private static partial class MeshGossipHostLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Mesh gossip round failed.")]
        public static partial void GossipRoundFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Mesh gossip sweep failed.")]
        public static partial void GossipSweepFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Mesh peer {PeerId} joined cluster {ClusterId} as {Role} (region {Region}, version {Version}, http3={Http3}).")]
        public static partial void PeerJoined(ILogger logger, string peerId, string clusterId, string role, string region, string version, bool http3);

        [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Mesh peer {PeerId} recovered from {PreviousStatus} (cluster {ClusterId}, role {Role}).")]
        public static partial void PeerRecovered(ILogger logger, string peerId, string previousStatus, string clusterId, string role);

        [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Mesh peer {PeerId} marked suspect (cluster {ClusterId}, role {Role}, lastSeen={LastSeen}).")]
        public static partial void PeerSuspect(ILogger logger, string peerId, string clusterId, string role, DateTimeOffset lastSeen);

        [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Mesh peer {PeerId} left gossip cluster {ClusterId} (role {Role}).")]
        public static partial void PeerLeft(ILogger logger, string peerId, string clusterId, string role);

        [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Failed to gossip with {Target}")]
        public static partial void GossipRequestFailed(ILogger logger, string target, Exception exception);

        [LoggerMessage(EventId = 8, Level = LogLevel.Debug, Message = "Mesh gossip host stop threw during dispose.")]
        public static partial void DisposalFailed(ILogger logger, Exception exception);
    }

    private sealed class ForwardingLoggerProvider(ILogger logger) : ILoggerProvider
    {
        private readonly ILogger _logger = logger;

        public ILogger CreateLogger(string categoryName) => new ForwardingLogger(_logger);

        public void Dispose()
        {
        }

        private sealed class ForwardingLogger(ILogger inner) : ILogger
        {
            private readonly ILogger _inner = inner;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                _inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
