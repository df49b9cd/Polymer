using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Grpc.Net.Compression;
using Hugo;
using Microsoft.Extensions.Logging;
using OmniRelay.Core;
using OmniRelay.Core.Peers;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using OmniRelay.Transport.Grpc.Interceptors;
using static Hugo.Go;

namespace OmniRelay.Transport.Grpc;

public sealed class GrpcOutbound : IUnaryOutbound, IOnewayOutbound, IStreamOutbound, IClientStreamOutbound, IDuplexOutbound, IOutboundDiagnostic, IGrpcClientInterceptorSink
{
    private readonly IReadOnlyList<Uri> _addresses;
    private readonly string _remoteService;
    private readonly GrpcChannelOptions _channelOptions;
    private readonly GrpcClientTlsOptions? _clientTlsOptions;
    private readonly GrpcClientRuntimeOptions? _clientRuntimeOptions;
    private readonly GrpcCompressionOptions? _compressionOptions;
    private readonly PeerCircuitBreakerOptions _peerBreakerOptions;
    private readonly GrpcTelemetryOptions? _telemetryOptions;
    private readonly Func<IReadOnlyList<IPeer>, IPeerChooser> _peerChooserFactory;
    private ImmutableArray<GrpcPeer> _peers = [];
    private IPeerChooser? _peerChooser;
    private readonly ConcurrentDictionary<string, Method<byte[], byte[]>> _unaryMethods = new();
    private readonly ConcurrentDictionary<string, Method<byte[], byte[]>> _serverStreamMethods = new();
    private readonly ConcurrentDictionary<string, Method<byte[], byte[]>> _clientStreamMethods = new();
    private readonly ConcurrentDictionary<string, Method<byte[], byte[]>> _duplexMethods = new();
    private readonly HashSet<string>? _compressionAlgorithms;
    private volatile bool _started;
    private CompositeClientInterceptor? _compositeClientInterceptor;
    private string? _interceptorService;
    private int _interceptorConfigured;

    public GrpcOutbound(
        Uri address,
        string remoteService,
        GrpcChannelOptions? channelOptions = null,
        GrpcClientTlsOptions? clientTlsOptions = null,
        Func<IReadOnlyList<IPeer>, IPeerChooser>? peerChooser = null,
        GrpcClientRuntimeOptions? clientRuntimeOptions = null,
        GrpcCompressionOptions? compressionOptions = null,
        PeerCircuitBreakerOptions? peerCircuitBreakerOptions = null,
        GrpcTelemetryOptions? telemetryOptions = null)
        : this(
            [address ?? throw new ArgumentNullException(nameof(address))],
            remoteService,
            channelOptions,
            clientTlsOptions,
            peerChooser,
            clientRuntimeOptions,
            compressionOptions,
            peerCircuitBreakerOptions,
            telemetryOptions)
    {
    }

    public GrpcOutbound(
        IEnumerable<Uri> addresses,
        string remoteService,
        GrpcChannelOptions? channelOptions = null,
        GrpcClientTlsOptions? clientTlsOptions = null,
        Func<IReadOnlyList<IPeer>, IPeerChooser>? peerChooser = null,
        GrpcClientRuntimeOptions? clientRuntimeOptions = null,
        GrpcCompressionOptions? compressionOptions = null,
        PeerCircuitBreakerOptions? peerCircuitBreakerOptions = null,
        GrpcTelemetryOptions? telemetryOptions = null)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var addressArray = addresses
            .Select(uri => uri ?? throw new ArgumentException("Peer address cannot be null.", nameof(addresses)))
            .ToArray();

        if (addressArray.Length == 0)
        {
            throw new ArgumentException("At least one address must be provided for the gRPC outbound.", nameof(addresses));
        }

        _addresses = addressArray;
        _remoteService = string.IsNullOrWhiteSpace(remoteService)
            ? throw new ArgumentException("Remote service name must be provided.", nameof(remoteService))
            : remoteService;
        _clientTlsOptions = clientTlsOptions;
        _clientRuntimeOptions = clientRuntimeOptions;
        _compressionOptions = compressionOptions;
        _telemetryOptions = telemetryOptions;
        _peerBreakerOptions = peerCircuitBreakerOptions ?? new PeerCircuitBreakerOptions();
        _channelOptions = channelOptions ?? new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true
            }
        };
        _peerChooserFactory = peerChooser ?? (peers => new RoundRobinPeerChooser(peers.ToImmutableArray()));

        if (_compressionOptions is { } compression)
        {
            compression.Validate();

            var providers = compression.Providers.Count > 0
                ? [.. compression.Providers]
                : new List<ICompressionProvider>();

            _compressionAlgorithms = new HashSet<string>(
                providers.Select(provider => provider.EncodingName),
                StringComparer.OrdinalIgnoreCase);

            _channelOptions.CompressionProviders = providers;
        }

        if (_clientRuntimeOptions is not null)
        {
            ApplyClientRuntimeOptions(_channelOptions, _clientRuntimeOptions);
        }

        if (_clientTlsOptions is not null)
        {
            ApplyClientTlsOptions(_channelOptions, _clientTlsOptions);
        }
        else if (_channelOptions.HttpHandler is null && _channelOptions.HttpClient is null)
        {
            _channelOptions.HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true
            };
        }
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return ValueTask.CompletedTask;
        }

        var builder = ImmutableArray.CreateBuilder<GrpcPeer>(_addresses.Count);
        foreach (var address in _addresses)
        {
            var peer = new GrpcPeer(address, this, _peerBreakerOptions);
            peer.Start();
            builder.Add(peer);
        }

        _peers = builder.ToImmutable();
        _peerChooser = _peerChooserFactory(_peers);

        _started = true;
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            return;
        }

        foreach (var peer in _peers)
        {
            await peer.DisposeAsync().ConfigureAwait(false);
        }

        _peers = [];
        _peerChooser = null;
        _started = false;

        _unaryMethods.Clear();
        _serverStreamMethods.Clear();
        _clientStreamMethods.Clear();
        _duplexMethods.Clear();
    }

    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            throw new InvalidOperationException("gRPC outbound has not been started.");
        }

        if (string.IsNullOrEmpty(request.Meta.Procedure))
        {
            return Err<Response<ReadOnlyMemory<byte>>>(
                OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.InvalidArgument, "Procedure metadata is required for gRPC calls.", transport: GrpcTransportConstants.TransportName));
        }

        var procedure = request.Meta.Procedure!;
        var acquireResult = await AcquirePeerAsync(request.Meta, cancellationToken).ConfigureAwait(false);
        if (acquireResult.IsFailure)
        {
            return Err<Response<ReadOnlyMemory<byte>>>(acquireResult.Error!);
        }

        var (lease, peer) = acquireResult.Value;
        await using var _ = lease.ConfigureAwait(false);

        using var activity = GrpcTransportDiagnostics.StartClientActivity(_remoteService, procedure, peer.Address, "unary");

        var method = _unaryMethods.GetOrAdd(procedure, CreateUnaryMethod);
        var callOptions = CreateCallOptions(request.Meta, cancellationToken);
        var callInvoker = peer.CallInvoker;

        try
        {
            var call = callInvoker.AsyncUnaryCall(method, null, callOptions, request.Body.ToArray());
            var response = await call.ResponseAsync.ConfigureAwait(false);

            var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var trailers = call.GetTrailers();
            var responseMeta = GrpcMetadataAdapter.CreateResponseMeta(headers, trailers);

            GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
            lease.MarkSuccess();
            return Ok(Response<ReadOnlyMemory<byte>>.Create(response, responseMeta));
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            GrpcTransportDiagnostics.RecordException(activity, rpcEx, rpcEx.Status.StatusCode, message);
            var error = OmniRelayErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            RecordPeerOutcome(lease, error);
            return Err<Response<ReadOnlyMemory<byte>>>(error);
        }
        catch (Exception ex)
        {
            GrpcTransportDiagnostics.RecordException(activity, ex, StatusCode.Unknown, ex.Message);
            var result = OmniRelayErrors.ToResult<Response<ReadOnlyMemory<byte>>>(ex, transport: GrpcTransportConstants.TransportName);
            RecordPeerOutcome(lease, result.Error!);
            return result;
        }
    }

    async ValueTask<Result<OnewayAck>> IOnewayOutbound.CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken)
    {
        if (!_started)
        {
            throw new InvalidOperationException("gRPC outbound has not been started.");
        }

        if (string.IsNullOrEmpty(request.Meta.Procedure))
        {
            return Err<OnewayAck>(
                OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.InvalidArgument, "Procedure metadata is required for gRPC calls.", transport: GrpcTransportConstants.TransportName));
        }

        var procedure = request.Meta.Procedure!;
        var acquireResult = await AcquirePeerAsync(request.Meta, cancellationToken).ConfigureAwait(false);
        if (acquireResult.IsFailure)
        {
            return Err<OnewayAck>(acquireResult.Error!);
        }

        var (lease, peer) = acquireResult.Value;
        await using var _ = lease.ConfigureAwait(false);

        using var activity = GrpcTransportDiagnostics.StartClientActivity(_remoteService, procedure, peer.Address, "oneway");

        var method = _unaryMethods.GetOrAdd(procedure, CreateUnaryMethod);
        var callOptions = CreateCallOptions(request.Meta, cancellationToken);
        var callInvoker = peer.CallInvoker;

        try
        {
            var call = callInvoker.AsyncUnaryCall(method, null, callOptions, request.Body.ToArray());
            await call.ResponseAsync.ConfigureAwait(false);

            var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var trailers = call.GetTrailers();
            var responseMeta = GrpcMetadataAdapter.CreateResponseMeta(headers, trailers);

            GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
            lease.MarkSuccess();
            return Ok(OnewayAck.Ack(responseMeta));
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            GrpcTransportDiagnostics.RecordException(activity, rpcEx, rpcEx.Status.StatusCode, message);
            var error = OmniRelayErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            RecordPeerOutcome(lease, error);
            return Err<OnewayAck>(error);
        }
        catch (Exception ex)
        {
            GrpcTransportDiagnostics.RecordException(activity, ex, StatusCode.Unknown, ex.Message);
            var result = OmniRelayErrors.ToResult<OnewayAck>(ex, transport: GrpcTransportConstants.TransportName);
            RecordPeerOutcome(lease, result.Error!);
            return result;
        }
    }

    public async ValueTask<Result<IStreamCall>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            throw new InvalidOperationException("gRPC outbound has not been started.");
        }

        if (options.Direction != StreamDirection.Server)
        {
            return Err<IStreamCall>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Unimplemented,
                "Only server streaming is currently supported over gRPC.",
                transport: GrpcTransportConstants.TransportName));
        }

        if (string.IsNullOrEmpty(request.Meta.Procedure))
        {
            return Err<IStreamCall>(
                OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.InvalidArgument, "Procedure metadata is required for gRPC streaming calls.", transport: GrpcTransportConstants.TransportName));
        }

        var procedure = request.Meta.Procedure!;
        var acquireResult = await AcquirePeerAsync(request.Meta, cancellationToken).ConfigureAwait(false);
        if (acquireResult.IsFailure)
        {
            return Err<IStreamCall>(acquireResult.Error!);
        }

        var (lease, peer) = acquireResult.Value;
        using var activity = GrpcTransportDiagnostics.StartClientActivity(_remoteService, procedure, peer.Address, "server_stream");

        var method = _serverStreamMethods.GetOrAdd(procedure, CreateServerStreamingMethod);
        var callOptions = CreateCallOptions(request.Meta, cancellationToken);
        var callInvoker = peer.CallInvoker;

        try
        {
            var call = callInvoker.AsyncServerStreamingCall(method, null, callOptions, request.Body.ToArray());
            var streamCallResult = await GrpcClientStreamCall.CreateAsync(request.Meta, call, cancellationToken).ConfigureAwait(false);
            if (streamCallResult.IsFailure)
            {
                call.Dispose();
                RecordPeerOutcome(lease, streamCallResult.Error!);
                await lease.DisposeAsync().ConfigureAwait(false);
                var exception = OmniRelayErrors.FromError(streamCallResult.Error!, GrpcTransportConstants.TransportName);
                var grpcStatus = GrpcStatusMapper.ToStatus(exception.StatusCode, exception.Message);
                GrpcTransportDiagnostics.RecordException(activity, exception, grpcStatus.StatusCode, exception.Message);
                return Err<IStreamCall>(streamCallResult.Error!);
            }

            GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
            lease.MarkSuccess();
            var wrapped = new PeerTrackedStreamCall(streamCallResult.Value, lease);
            return Ok((IStreamCall)wrapped);
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            GrpcTransportDiagnostics.RecordException(activity, rpcEx, rpcEx.Status.StatusCode, message);
            var error = OmniRelayErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            RecordPeerOutcome(lease, error);
            await lease.DisposeAsync().ConfigureAwait(false);
            return Err<IStreamCall>(error);
        }
        catch (Exception ex)
        {
            GrpcTransportDiagnostics.RecordException(activity, ex, StatusCode.Unknown, ex.Message);
            var result = OmniRelayErrors.ToResult<IStreamCall>(ex, transport: GrpcTransportConstants.TransportName);
            RecordPeerOutcome(lease, result.Error!);
            await lease.DisposeAsync().ConfigureAwait(false);
            return result;
        }
    }

    public async ValueTask<Result<IClientStreamTransportCall>> CallAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            throw new InvalidOperationException("gRPC outbound has not been started.");
        }

        ArgumentNullException.ThrowIfNull(requestMeta);

        if (string.IsNullOrEmpty(requestMeta.Procedure))
        {
            return Err<IClientStreamTransportCall>(
                OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.InvalidArgument,
                    "Procedure metadata is required for gRPC client streaming calls.",
                    transport: GrpcTransportConstants.TransportName));
        }

        var procedure = requestMeta.Procedure!;
        var acquireResult = await AcquirePeerAsync(requestMeta, cancellationToken).ConfigureAwait(false);
        if (acquireResult.IsFailure)
        {
            return Err<IClientStreamTransportCall>(acquireResult.Error!);
        }

        var (lease, peer) = acquireResult.Value;
        using var activity = GrpcTransportDiagnostics.StartClientActivity(_remoteService, procedure, peer.Address, "client_stream");

        var method = _clientStreamMethods.GetOrAdd(procedure, CreateClientStreamingMethod);
        var callOptions = CreateCallOptions(requestMeta, cancellationToken);
        var callInvoker = peer.CallInvoker;

        try
        {
            var call = callInvoker.AsyncClientStreamingCall(method, null, callOptions);
            var streamingCall = new GrpcClientStreamTransportCall(requestMeta, call, null);
            lease.MarkSuccess();
            var wrapped = new PeerTrackedClientStreamCall(streamingCall, lease);
            GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
            return Ok((IClientStreamTransportCall)wrapped);
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            GrpcTransportDiagnostics.RecordException(activity, rpcEx, rpcEx.Status.StatusCode, message);
            var error = OmniRelayErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            RecordPeerOutcome(lease, error);
            await lease.DisposeAsync().ConfigureAwait(false);
            return Err<IClientStreamTransportCall>(error);
        }
        catch (Exception ex)
        {
            GrpcTransportDiagnostics.RecordException(activity, ex, StatusCode.Unknown, ex.Message);
            var result = OmniRelayErrors.ToResult<IClientStreamTransportCall>(ex, transport: GrpcTransportConstants.TransportName);
            RecordPeerOutcome(lease, result.Error!);
            await lease.DisposeAsync().ConfigureAwait(false);
            return result;
        }
    }

    async ValueTask<Result<IDuplexStreamCall>> IDuplexOutbound.CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken)
    {
        if (!_started)
        {
            throw new InvalidOperationException("gRPC outbound has not been started.");
        }

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(request.Meta.Procedure))
        {
            return Err<IDuplexStreamCall>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                "Procedure metadata is required for gRPC duplex streaming calls.",
                transport: GrpcTransportConstants.TransportName));
        }

        var procedure = request.Meta.Procedure!;
        var acquireResult = await AcquirePeerAsync(request.Meta, cancellationToken).ConfigureAwait(false);
        if (acquireResult.IsFailure)
        {
            return Err<IDuplexStreamCall>(acquireResult.Error!);
        }

        var (lease, peer) = acquireResult.Value;
        using var activity = GrpcTransportDiagnostics.StartClientActivity(_remoteService, procedure, peer.Address, "bidi_stream");

        var method = _duplexMethods.GetOrAdd(procedure, CreateDuplexStreamingMethod);
        var callOptions = CreateCallOptions(request.Meta, cancellationToken);
        var callInvoker = peer.CallInvoker;

        try
        {
            var call = callInvoker.AsyncDuplexStreamingCall(method, null, callOptions);
            var duplexResult = await GrpcDuplexStreamTransportCall.CreateAsync(request.Meta, call, cancellationToken).ConfigureAwait(false);
            if (duplexResult.IsFailure)
            {
                call.Dispose();
                RecordPeerOutcome(lease, duplexResult.Error!);
                await lease.DisposeAsync().ConfigureAwait(false);
                var exception = OmniRelayErrors.FromError(duplexResult.Error!, GrpcTransportConstants.TransportName);
                var grpcStatus = GrpcStatusMapper.ToStatus(exception.StatusCode, exception.Message);
                GrpcTransportDiagnostics.RecordException(activity, exception, grpcStatus.StatusCode, exception.Message);
                return Err<IDuplexStreamCall>(duplexResult.Error!);
            }

            GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
            lease.MarkSuccess();
            var wrapped = new PeerTrackedDuplexStreamCall(duplexResult.Value, lease);
            return Ok<IDuplexStreamCall>(wrapped);
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            GrpcTransportDiagnostics.RecordException(activity, rpcEx, rpcEx.Status.StatusCode, message);
            var error = OmniRelayErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            RecordPeerOutcome(lease, error);
            await lease.DisposeAsync().ConfigureAwait(false);
            return Err<IDuplexStreamCall>(error);
        }
        catch (Exception ex)
        {
            GrpcTransportDiagnostics.RecordException(activity, ex, StatusCode.Unknown, ex.Message);
            var result = OmniRelayErrors.ToResult<IDuplexStreamCall>(ex, transport: GrpcTransportConstants.TransportName);
            RecordPeerOutcome(lease, result.Error!);
            await lease.DisposeAsync().ConfigureAwait(false);
            return result;
        }
    }

    public object? GetOutboundDiagnostics()
    {
        var algorithms = _compressionAlgorithms is { Count: > 0 }
            ? _compressionAlgorithms.ToArray()
            : [];

        var chooserName = _peerChooser is null
            ? "none"
            : _peerChooser.GetType().FullName ?? _peerChooser.GetType().Name;

        var peers = _peers
            .Select(peer =>
            {
                var status = peer.Status;
                var latency = peer.GetLatencySnapshot();
                return new GrpcPeerSummary(
                    peer.Address,
                    status.State,
                    status.Inflight,
                    status.LastSuccess,
                    status.LastFailure,
                    peer.SuccessCount,
                    peer.FailureCount,
                    latency.HasData ? latency.Average : null,
                    latency.HasData ? latency.P50 : null,
                    latency.HasData ? latency.P90 : null,
                    latency.HasData ? latency.P99 : null);
            })
            .ToImmutableArray();

        return new GrpcOutboundSnapshot(
            _remoteService,
            _addresses.ToArray(),
            chooserName,
            _started,
            peers,
            algorithms);
    }

    private Method<byte[], byte[]> CreateUnaryMethod(string procedure) =>
        new(
            MethodType.Unary,
            _remoteService,
            procedure,
            GrpcMarshallerCache.ByteMarshaller,
            GrpcMarshallerCache.ByteMarshaller);

    private Method<byte[], byte[]> CreateServerStreamingMethod(string procedure) =>
        new(
            MethodType.ServerStreaming,
            _remoteService,
            procedure,
            GrpcMarshallerCache.ByteMarshaller,
            GrpcMarshallerCache.ByteMarshaller);

    private Method<byte[], byte[]> CreateClientStreamingMethod(string procedure) =>
        new(
            MethodType.ClientStreaming,
            _remoteService,
            procedure,
            GrpcMarshallerCache.ByteMarshaller,
            GrpcMarshallerCache.ByteMarshaller);

    private Method<byte[], byte[]> CreateDuplexStreamingMethod(string procedure) =>
        new(
            MethodType.DuplexStreaming,
            _remoteService,
            procedure,
            GrpcMarshallerCache.ByteMarshaller,
            GrpcMarshallerCache.ByteMarshaller);

    private CallOptions CreateCallOptions(RequestMeta meta, CancellationToken cancellationToken)
    {
        var metadata = GrpcMetadataAdapter.CreateRequestMetadata(meta);
        var deadline = ResolveDeadline(meta);
        var callOptions = deadline.HasValue
            ? new CallOptions(metadata, deadline.Value, cancellationToken)
            : new CallOptions(metadata, cancellationToken: cancellationToken);

        if (_compressionAlgorithms is { Count: > 0 } algorithms &&
            metadata.GetValue(GrpcTransportConstants.GrpcAcceptEncodingHeader) is null)
        {
            metadata.Add(GrpcTransportConstants.GrpcAcceptEncodingHeader, string.Join(",", algorithms));
        }

        return callOptions;
    }

    private async ValueTask<Result<(PeerLease Lease, GrpcPeer Peer)>> AcquirePeerAsync(RequestMeta meta, CancellationToken cancellationToken)
    {
        if (_peerChooser is null)
        {
            var error = OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Unavailable,
                "gRPC outbound has not been started.",
                transport: meta.Transport ?? GrpcTransportConstants.TransportName);
            return Err<(PeerLease, GrpcPeer)>(error);
        }

        var leaseResult = await _peerChooser.AcquireAsync(meta, cancellationToken).ConfigureAwait(false);
        if (leaseResult.IsFailure)
        {
            return Err<(PeerLease, GrpcPeer)>(leaseResult.Error!);
        }

        var lease = leaseResult.Value;
        if (lease.Peer is not GrpcPeer grpcPeer)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            var error = OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Internal,
                "Peer chooser returned an incompatible peer instance for gRPC.",
                transport: meta.Transport ?? GrpcTransportConstants.TransportName);
            return Err<(PeerLease, GrpcPeer)>(error);
        }

        return Ok((lease, grpcPeer));
    }

    private static DateTime? ResolveDeadline(RequestMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        DateTime? resolved = null;

        if (meta.Deadline is { } absoluteDeadline)
        {
            var utcDeadline = absoluteDeadline.ToUniversalTime().UtcDateTime;
            resolved = utcDeadline.Kind == DateTimeKind.Utc
                ? utcDeadline
                : DateTime.SpecifyKind(utcDeadline, DateTimeKind.Utc);
        }

        if (meta.TimeToLive is { } ttl && ttl > TimeSpan.Zero)
        {
            var ttlDeadline = DateTime.UtcNow.Add(ttl);
            resolved = resolved.HasValue
                ? (resolved.Value <= ttlDeadline ? resolved.Value : ttlDeadline)
                : ttlDeadline;
        }

        return resolved;
    }

    void IGrpcClientInterceptorSink.AttachGrpcClientInterceptors(string service, GrpcClientInterceptorRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (Interlocked.Exchange(ref _interceptorConfigured, 1) == 1)
        {
            return;
        }

        _interceptorService = string.IsNullOrWhiteSpace(service) ? string.Empty : service;
        _compositeClientInterceptor = new CompositeClientInterceptor(registry, _interceptorService);
    }

    private sealed class GrpcPeer(Uri address, GrpcOutbound owner, PeerCircuitBreakerOptions breakerOptions) : IPeer, IAsyncDisposable, IPeerTelemetry
    {
        private readonly GrpcOutbound _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        private readonly Uri _address = address ?? throw new ArgumentNullException(nameof(address));
        private readonly PeerCircuitBreaker _breaker = new(breakerOptions);
        private GrpcChannel? _channel;
        private CallInvoker? _callInvoker;
        private int _inflight;
        private PeerState _state = PeerState.Unknown;
        private DateTimeOffset? _lastSuccess;
        private DateTimeOffset? _lastFailure;
        private long _successCount;
        private long _failureCount;
        private readonly LatencyTracker _latencyTracker = new();

        public Uri Address => _address;

        public string Identifier => _address.ToString();

        public PeerStatus Status
        {
            get
            {
                var inflight = Volatile.Read(ref _inflight);
                var state = _breaker.IsSuspended ? PeerState.Unavailable : _state;
                return new PeerStatus(state, inflight, _lastSuccess, _lastFailure);
            }
        }

        public long SuccessCount => Interlocked.Read(ref _successCount);

        public long FailureCount => Interlocked.Read(ref _failureCount);

        public LatencySnapshot GetLatencySnapshot() => _latencyTracker.Snapshot();

        public CallInvoker CallInvoker => _callInvoker ?? throw new InvalidOperationException("Peer has not been started.");

        public void Start()
        {
            var options = CloneChannelOptions();
            _channel = GrpcChannel.ForAddress(_address, options);
            var invoker = _channel.CreateCallInvoker();

            var interceptors = new List<Interceptor>();

            if (_owner._compositeClientInterceptor is { } composite)
            {
                interceptors.Add(composite);
            }

            if (_owner._clientRuntimeOptions is { Interceptors.Count: > 0 } runtimeOptions)
            {
                foreach (var interceptor in runtimeOptions.Interceptors)
                {
                    if (interceptor is not null)
                    {
                        interceptors.Add(interceptor);
                    }
                }
            }

            if (_owner._telemetryOptions?.EnableClientLogging == true)
            {
                var loggerFactory = _owner._telemetryOptions.ResolveLoggerFactory();
                interceptors.Add(new GrpcClientLoggingInterceptor(loggerFactory.CreateLogger<GrpcClientLoggingInterceptor>()));
            }

            if (interceptors.Count > 0)
            {
                invoker = invoker.Intercept([.. interceptors]);
            }

            _callInvoker = invoker;
            _state = PeerState.Available;
            _breaker.OnSuccess();
        }

        public bool TryAcquire(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_breaker.TryEnter())
            {
                return false;
            }

            Interlocked.Increment(ref _inflight);
            return true;
        }

        public void Release(bool success)
        {
            Interlocked.Decrement(ref _inflight);

            if (success)
            {
                _breaker.OnSuccess();
                _lastSuccess = DateTimeOffset.UtcNow;
                _state = PeerState.Available;
            }
            else
            {
                _breaker.OnFailure();
                _lastFailure = DateTimeOffset.UtcNow;
                _state = _breaker.IsSuspended ? PeerState.Unavailable : PeerState.Available;
            }
        }

        public void RecordLeaseResult(bool success, double durationMilliseconds)
        {
            if (success)
            {
                Interlocked.Increment(ref _successCount);
            }
            else
            {
                Interlocked.Increment(ref _failureCount);
            }

            _latencyTracker.Record(durationMilliseconds);
        }

        public ValueTask DisposeAsync()
        {
            var channel = Interlocked.Exchange(ref _channel, null);
            channel?.Dispose();

            _callInvoker = null;
            _state = PeerState.Unknown;
            _inflight = 0;
            return ValueTask.CompletedTask;
        }

        private GrpcChannelOptions CloneChannelOptions()
        {
            var options = new GrpcChannelOptions
            {
                LoggerFactory = _owner._channelOptions.LoggerFactory,
                DisposeHttpClient = _owner._channelOptions.DisposeHttpClient,
                Credentials = _owner._channelOptions.Credentials,
                CompressionProviders = _owner._channelOptions.CompressionProviders,
                MaxReceiveMessageSize = _owner._channelOptions.MaxReceiveMessageSize,
                MaxSendMessageSize = _owner._channelOptions.MaxSendMessageSize,
                UnsafeUseInsecureChannelCallCredentials = _owner._channelOptions.UnsafeUseInsecureChannelCallCredentials
            };

            if (_owner._channelOptions.HttpHandler is not null)
            {
                options.HttpHandler = _owner._channelOptions.HttpHandler;
            }

            if (_owner._channelOptions.HttpClient is not null)
            {
                options.HttpClient = _owner._channelOptions.HttpClient;
            }

            return options;
        }
    }

    private sealed class LatencyTracker
    {
        private readonly double[] _buffer;
        private readonly Lock _lock = new();
        private int _count;
        private int _nextIndex;
        private double _sum;

        public LatencyTracker(int capacity = 256)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

            _buffer = new double[capacity];
        }

        public void Record(double durationMilliseconds)
        {
            if (double.IsNaN(durationMilliseconds) || double.IsInfinity(durationMilliseconds))
            {
                return;
            }

            var value = Math.Max(0, durationMilliseconds);

            lock (_lock)
            {
                if (_count == _buffer.Length)
                {
                    _sum -= _buffer[_nextIndex];
                }
                else
                {
                    _count++;
                }

                _buffer[_nextIndex] = value;
                _sum += value;
                _nextIndex = (_nextIndex + 1) % _buffer.Length;
            }
        }

        public LatencySnapshot Snapshot()
        {
            lock (_lock)
            {
                if (_count == 0)
                {
                    return LatencySnapshot.Empty;
                }

                var samples = new double[_count];
                if (_count == _buffer.Length)
                {
                    for (var i = 0; i < _count; i++)
                    {
                        var index = (_nextIndex + i) % _buffer.Length;
                        samples[i] = _buffer[index];
                    }
                }
                else
                {
                    Array.Copy(_buffer, samples, _count);
                }

                Array.Sort(samples);
                var average = _sum / _count;
                var p50 = CalculatePercentile(samples, 0.50);
                var p90 = CalculatePercentile(samples, 0.90);
                var p99 = CalculatePercentile(samples, 0.99);
                return new LatencySnapshot(average, p50, p90, p99);
            }
        }

        private static double CalculatePercentile(double[] samples, double percentile)
        {
            if (samples.Length == 0)
            {
                return double.NaN;
            }

            var position = percentile * (samples.Length - 1);
            var lowerIndex = (int)Math.Floor(position);
            var upperIndex = (int)Math.Ceiling(position);

            if (lowerIndex == upperIndex)
            {
                return samples[lowerIndex];
            }

            var fraction = position - lowerIndex;
            return samples[lowerIndex] + (samples[upperIndex] - samples[lowerIndex]) * fraction;
        }
    }

    internal readonly struct LatencySnapshot(double average, double p50, double p90, double p99)
    {
        public static LatencySnapshot Empty { get; } = new(double.NaN, double.NaN, double.NaN, double.NaN);

        public double Average { get; } = average;

        public double P50 { get; } = p50;

        public double P90 { get; } = p90;

        public double P99 { get; } = p99;

        public bool HasData => !double.IsNaN(Average);
    }

    private sealed class PeerTrackedStreamCall(IStreamCall inner, PeerLease lease) : IStreamCall
    {
        private readonly IStreamCall _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        private readonly PeerLease _lease = lease ?? throw new ArgumentNullException(nameof(lease));

        public StreamDirection Direction => _inner.Direction;
        public RequestMeta RequestMeta => _inner.RequestMeta;
        public ResponseMeta ResponseMeta => _inner.ResponseMeta;
        public StreamCallContext Context => _inner.Context;
        public ChannelWriter<ReadOnlyMemory<byte>> Requests => _inner.Requests;
        public ChannelReader<ReadOnlyMemory<byte>> Responses => _inner.Responses;

        public ValueTask CompleteAsync(Error? error = null, CancellationToken cancellationToken = default) =>
            _inner.CompleteAsync(error, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class PeerTrackedClientStreamCall(IClientStreamTransportCall inner, PeerLease lease) : IClientStreamTransportCall
    {
        private readonly IClientStreamTransportCall _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        private readonly PeerLease _lease = lease ?? throw new ArgumentNullException(nameof(lease));

        public RequestMeta RequestMeta => _inner.RequestMeta;
        public ResponseMeta ResponseMeta => _inner.ResponseMeta;
        public Task<Result<Response<ReadOnlyMemory<byte>>>> Response => _inner.Response;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(payload, cancellationToken);

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) =>
            _inner.CompleteAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class PeerTrackedDuplexStreamCall(IDuplexStreamCall inner, PeerLease lease) : IDuplexStreamCall
    {
        private readonly IDuplexStreamCall _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        private readonly PeerLease _lease = lease ?? throw new ArgumentNullException(nameof(lease));

        public RequestMeta RequestMeta => _inner.RequestMeta;
        public ResponseMeta ResponseMeta => _inner.ResponseMeta;
        public DuplexStreamCallContext Context => _inner.Context;
        public ChannelWriter<ReadOnlyMemory<byte>> RequestWriter => _inner.RequestWriter;
        public ChannelReader<ReadOnlyMemory<byte>> RequestReader => _inner.RequestReader;
        public ChannelWriter<ReadOnlyMemory<byte>> ResponseWriter => _inner.ResponseWriter;
        public ChannelReader<ReadOnlyMemory<byte>> ResponseReader => _inner.ResponseReader;

        public ValueTask CompleteRequestsAsync(Error? error = null, CancellationToken cancellationToken = default) =>
            _inner.CompleteRequestsAsync(error, cancellationToken);

        public ValueTask CompleteResponsesAsync(Error? error = null, CancellationToken cancellationToken = default) =>
            _inner.CompleteResponsesAsync(error, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static void ApplyClientTlsOptions(GrpcChannelOptions channelOptions, GrpcClientTlsOptions tlsOptions)
    {
        if (channelOptions.HttpClient is not null)
        {
            throw new InvalidOperationException("Cannot apply gRPC client TLS options when a custom HttpClient is provided.");
        }

        var handler = GetOrCreateSocketsHandler(channelOptions);
        var sslOptions = handler.SslOptions;

        if (tlsOptions.EnabledProtocols is { } protocols)
        {
            sslOptions.EnabledSslProtocols = protocols;
        }

        if (tlsOptions.CheckCertificateRevocation is { } checkRevocation)
        {
            sslOptions.CertificateRevocationCheckMode = checkRevocation
                ? X509RevocationMode.Online
                : X509RevocationMode.NoCheck;
        }

        if (tlsOptions.ServerCertificateValidationCallback is { } validationCallback)
        {
            sslOptions.RemoteCertificateValidationCallback = validationCallback;
        }

        if (tlsOptions.EncryptionPolicy is { } encryptionPolicy)
        {
            sslOptions.EncryptionPolicy = encryptionPolicy;
        }

        if (tlsOptions.ClientCertificates.Count > 0)
        {
            sslOptions.ClientCertificates = tlsOptions.ClientCertificates;
        }
    }

    private static void ApplyClientRuntimeOptions(GrpcChannelOptions channelOptions, GrpcClientRuntimeOptions runtimeOptions)
    {
        if (runtimeOptions is null)
        {
            return;
        }

        if (runtimeOptions.MaxReceiveMessageSize is { } maxReceive)
        {
            channelOptions.MaxReceiveMessageSize = maxReceive;
        }

        if (runtimeOptions.MaxSendMessageSize is { } maxSend)
        {
            channelOptions.MaxSendMessageSize = maxSend;
        }

        if (runtimeOptions.KeepAlivePingDelay is null &&
            runtimeOptions.KeepAlivePingTimeout is null &&
            runtimeOptions.KeepAlivePingPolicy is null)
        {
            return;
        }

        if (channelOptions.HttpClient is not null)
        {
            throw new InvalidOperationException("Cannot apply gRPC client runtime options when a custom HttpClient is provided.");
        }

        var handler = GetOrCreateSocketsHandler(channelOptions);

        if (runtimeOptions.KeepAlivePingDelay is { } delay)
        {
            handler.KeepAlivePingDelay = delay;
        }

        if (runtimeOptions.KeepAlivePingTimeout is { } timeout)
        {
            handler.KeepAlivePingTimeout = timeout;
        }

        if (runtimeOptions.KeepAlivePingPolicy is { } policy)
        {
            handler.KeepAlivePingPolicy = policy;
        }
    }

    private static void RecordPeerOutcome(PeerLease lease, Error error)
    {
        if (lease is null)
        {
            return;
        }

        if (ShouldPenalizePeer(error))
        {
            lease.MarkFailure();
            return;
        }

        lease.MarkSuccess();
    }

    private static bool ShouldPenalizePeer(Error error)
    {
        if (error is null)
        {
            return true;
        }

        var faultType = OmniRelayErrors.GetFaultType(error);
        return faultType != OmniRelayFaultType.Client;
    }

    private static SocketsHttpHandler GetOrCreateSocketsHandler(GrpcChannelOptions channelOptions)
    {
        if (channelOptions.HttpHandler is null)
        {
            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true
            };
            channelOptions.HttpHandler = handler;
            return handler;
        }

        if (channelOptions.HttpHandler is SocketsHttpHandler socketsHandler)
        {
            return socketsHandler;
        }

        throw new InvalidOperationException("gRPC client configuration requires a SocketsHttpHandler.");
    }
}

public sealed record GrpcOutboundSnapshot(
    string RemoteService,
    IReadOnlyList<Uri> Peers,
    string PeerChooser,
    bool IsStarted,
    IReadOnlyList<GrpcPeerSummary> PeerSummaries,
    IReadOnlyList<string> CompressionAlgorithms);

public sealed record GrpcPeerSummary(
    Uri Address,
    PeerState State,
    int Inflight,
    DateTimeOffset? LastSuccess,
    DateTimeOffset? LastFailure,
    long SuccessCount,
    long FailureCount,
    double? AverageLatencyMs,
    double? P50LatencyMs,
    double? P90LatencyMs,
    double? P99LatencyMs);
