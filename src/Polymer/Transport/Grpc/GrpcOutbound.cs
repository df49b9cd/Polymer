using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Hugo;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Transport.Grpc;

public sealed class GrpcOutbound : IUnaryOutbound, IOnewayOutbound, IStreamOutbound, IClientStreamOutbound, IDuplexOutbound
{
    private readonly IReadOnlyList<Uri> _addresses;
    private readonly string _remoteService;
    private readonly GrpcChannelOptions _channelOptions;
    private readonly GrpcClientTlsOptions? _clientTlsOptions;
    private readonly GrpcClientRuntimeOptions? _clientRuntimeOptions;
    private readonly IGrpcPeerChooser _peerChooser;
    private readonly ConcurrentDictionary<Uri, GrpcChannel> _channels = new();
    private readonly ConcurrentDictionary<Uri, CallInvoker> _callInvokers = new();
    private readonly ConcurrentDictionary<string, Method<byte[], byte[]>> _unaryMethods = new();
    private readonly ConcurrentDictionary<string, Method<byte[], byte[]>> _serverStreamMethods = new();
    private readonly ConcurrentDictionary<string, Method<byte[], byte[]>> _clientStreamMethods = new();
    private readonly ConcurrentDictionary<string, Method<byte[], byte[]>> _duplexMethods = new();
    private volatile bool _started;

    public GrpcOutbound(
        Uri address,
        string remoteService,
        GrpcChannelOptions? channelOptions = null,
        GrpcClientTlsOptions? clientTlsOptions = null,
        IGrpcPeerChooser? peerChooser = null,
        GrpcClientRuntimeOptions? clientRuntimeOptions = null)
        : this(
            new[] { address ?? throw new ArgumentNullException(nameof(address)) },
            remoteService,
            channelOptions,
            clientTlsOptions,
            peerChooser,
            clientRuntimeOptions)
    {
    }

    public GrpcOutbound(
        IEnumerable<Uri> addresses,
        string remoteService,
        GrpcChannelOptions? channelOptions = null,
        GrpcClientTlsOptions? clientTlsOptions = null,
        IGrpcPeerChooser? peerChooser = null,
        GrpcClientRuntimeOptions? clientRuntimeOptions = null)
    {
        if (addresses is null)
        {
            throw new ArgumentNullException(nameof(addresses));
        }

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
        _channelOptions = channelOptions ?? new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true
            }
        };
        _peerChooser = peerChooser ?? new RoundRobinGrpcPeerChooser();

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

        _channels.Clear();
        _callInvokers.Clear();

        foreach (var address in _addresses)
        {
            var channel = GrpcChannel.ForAddress(address, _channelOptions);
            _channels[address] = channel;
            _callInvokers[address] = channel.CreateCallInvoker();
        }

        _started = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var channel in _channels.Values)
        {
            channel.Dispose();
        }

        _channels.Clear();
        _callInvokers.Clear();
        _started = false;

        _unaryMethods.Clear();
        _serverStreamMethods.Clear();
        _clientStreamMethods.Clear();
        _duplexMethods.Clear();
        return ValueTask.CompletedTask;
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
                PolymerErrorAdapter.FromStatus(PolymerStatusCode.InvalidArgument, "Procedure metadata is required for gRPC calls.", transport: GrpcTransportConstants.TransportName));
        }

        var procedure = request.Meta.Procedure!;
        var (peerAddress, callInvoker) = GetCallInvoker(request.Meta);
        using var activity = GrpcTransportDiagnostics.StartClientActivity(_remoteService, procedure, peerAddress, "unary");

        var method = _unaryMethods.GetOrAdd(procedure, CreateUnaryMethod);
        var callOptions = CreateCallOptions(request.Meta, cancellationToken);

        try
        {
            var call = callInvoker.AsyncUnaryCall(method, null, callOptions, request.Body.ToArray());
            var response = await call.ResponseAsync.ConfigureAwait(false);

            var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var trailers = call.GetTrailers();
            var responseMeta = GrpcMetadataAdapter.CreateResponseMeta(headers, trailers, GrpcTransportConstants.TransportName);

            GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
            return Ok(Response<ReadOnlyMemory<byte>>.Create(response, responseMeta));
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            GrpcTransportDiagnostics.RecordException(activity, rpcEx, rpcEx.Status.StatusCode, message);
            var error = PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            return Err<Response<ReadOnlyMemory<byte>>>(error);
        }
        catch (Exception ex)
        {
            GrpcTransportDiagnostics.RecordException(activity, ex, StatusCode.Unknown, ex.Message);
            return PolymerErrors.ToResult<Response<ReadOnlyMemory<byte>>>(ex, transport: GrpcTransportConstants.TransportName);
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
                PolymerErrorAdapter.FromStatus(PolymerStatusCode.InvalidArgument, "Procedure metadata is required for gRPC calls.", transport: GrpcTransportConstants.TransportName));
        }

        var procedure = request.Meta.Procedure!;
        var (peerAddress, callInvoker) = GetCallInvoker(request.Meta);
        using var activity = GrpcTransportDiagnostics.StartClientActivity(_remoteService, procedure, peerAddress, "oneway");

        var method = _unaryMethods.GetOrAdd(procedure, CreateUnaryMethod);
        var callOptions = CreateCallOptions(request.Meta, cancellationToken);

        try
        {
            var call = callInvoker.AsyncUnaryCall(method, null, callOptions, request.Body.ToArray());
            await call.ResponseAsync.ConfigureAwait(false);

            var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var trailers = call.GetTrailers();
            var responseMeta = GrpcMetadataAdapter.CreateResponseMeta(headers, trailers, GrpcTransportConstants.TransportName);

            GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
            return Ok(OnewayAck.Ack(responseMeta));
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            GrpcTransportDiagnostics.RecordException(activity, rpcEx, rpcEx.Status.StatusCode, message);
            var error = PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            return Err<OnewayAck>(error);
        }
        catch (Exception ex)
        {
            GrpcTransportDiagnostics.RecordException(activity, ex, StatusCode.Unknown, ex.Message);
            return PolymerErrors.ToResult<OnewayAck>(ex, transport: GrpcTransportConstants.TransportName);
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
            return Err<IStreamCall>(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Unimplemented,
                "Only server streaming is currently supported over gRPC.",
                transport: GrpcTransportConstants.TransportName));
        }

        if (string.IsNullOrEmpty(request.Meta.Procedure))
        {
            return Err<IStreamCall>(
                PolymerErrorAdapter.FromStatus(PolymerStatusCode.InvalidArgument, "Procedure metadata is required for gRPC streaming calls.", transport: GrpcTransportConstants.TransportName));
        }

        var procedure = request.Meta.Procedure!;
        var (peerAddress, callInvoker) = GetCallInvoker(request.Meta);
        using var activity = GrpcTransportDiagnostics.StartClientActivity(_remoteService, procedure, peerAddress, "server_stream");

        var method = _serverStreamMethods.GetOrAdd(procedure, CreateServerStreamingMethod);
        var callOptions = CreateCallOptions(request.Meta, cancellationToken);

        try
        {
            var call = callInvoker.AsyncServerStreamingCall(method, null, callOptions, request.Body.ToArray());
            var streamCallResult = await GrpcClientStreamCall.CreateAsync(request.Meta, call, cancellationToken).ConfigureAwait(false);
            if (streamCallResult.IsFailure)
            {
                call.Dispose();
                var exception = PolymerErrors.FromError(streamCallResult.Error!, GrpcTransportConstants.TransportName);
                var grpcStatus = GrpcStatusMapper.ToStatus(exception.StatusCode, exception.Message);
                GrpcTransportDiagnostics.RecordException(activity, exception, grpcStatus.StatusCode, exception.Message);
                return Err<IStreamCall>(streamCallResult.Error!);
            }

            GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
            return Ok((IStreamCall)streamCallResult.Value);
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            GrpcTransportDiagnostics.RecordException(activity, rpcEx, rpcEx.Status.StatusCode, message);
            var error = PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            return Err<IStreamCall>(error);
        }
        catch (Exception ex)
        {
            GrpcTransportDiagnostics.RecordException(activity, ex, StatusCode.Unknown, ex.Message);
            return PolymerErrors.ToResult<IStreamCall>(ex, transport: GrpcTransportConstants.TransportName);
        }
    }

    public ValueTask<Result<IClientStreamTransportCall>> CallAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            throw new InvalidOperationException("gRPC outbound has not been started.");
        }

        if (requestMeta is null)
        {
            throw new ArgumentNullException(nameof(requestMeta));
        }

        if (string.IsNullOrEmpty(requestMeta.Procedure))
        {
            return ValueTask.FromResult(Err<IClientStreamTransportCall>(
                PolymerErrorAdapter.FromStatus(
                    PolymerStatusCode.InvalidArgument,
                    "Procedure metadata is required for gRPC client streaming calls.",
                    transport: GrpcTransportConstants.TransportName)));
        }

        var procedure = requestMeta.Procedure!;
        var (peerAddress, callInvoker) = GetCallInvoker(requestMeta);
        using var activity = GrpcTransportDiagnostics.StartClientActivity(_remoteService, procedure, peerAddress, "client_stream");

        var method = _clientStreamMethods.GetOrAdd(procedure, CreateClientStreamingMethod);
        var callOptions = CreateCallOptions(requestMeta, cancellationToken);

        try
        {
            var call = callInvoker.AsyncClientStreamingCall(method, null, callOptions);
            var streamingCall = new GrpcClientStreamTransportCall(requestMeta, call, null);
            GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
            return ValueTask.FromResult(Ok((IClientStreamTransportCall)streamingCall));
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            GrpcTransportDiagnostics.RecordException(activity, rpcEx, rpcEx.Status.StatusCode, message);
            var error = PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            return ValueTask.FromResult(Err<IClientStreamTransportCall>(error));
        }
        catch (Exception ex)
        {
            GrpcTransportDiagnostics.RecordException(activity, ex, StatusCode.Unknown, ex.Message);
            return ValueTask.FromResult(PolymerErrors.ToResult<IClientStreamTransportCall>(ex, transport: GrpcTransportConstants.TransportName));
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

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrEmpty(request.Meta.Procedure))
        {
            return Err<IDuplexStreamCall>(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.InvalidArgument,
                "Procedure metadata is required for gRPC duplex streaming calls.",
                transport: GrpcTransportConstants.TransportName));
        }

        var procedure = request.Meta.Procedure!;
        var (peerAddress, callInvoker) = GetCallInvoker(request.Meta);
        using var activity = GrpcTransportDiagnostics.StartClientActivity(_remoteService, procedure, peerAddress, "bidi_stream");

        var method = _duplexMethods.GetOrAdd(procedure, CreateDuplexStreamingMethod);
        var callOptions = CreateCallOptions(request.Meta, cancellationToken);

        try
        {
            var call = callInvoker.AsyncDuplexStreamingCall(method, null, callOptions);
            var duplexResult = await GrpcDuplexStreamTransportCall.CreateAsync(request.Meta, call, cancellationToken).ConfigureAwait(false);
            if (duplexResult.IsFailure)
            {
                call.Dispose();
                var exception = PolymerErrors.FromError(duplexResult.Error!, GrpcTransportConstants.TransportName);
                var grpcStatus = GrpcStatusMapper.ToStatus(exception.StatusCode, exception.Message);
                GrpcTransportDiagnostics.RecordException(activity, exception, grpcStatus.StatusCode, exception.Message);
                return Err<IDuplexStreamCall>(duplexResult.Error!);
            }

            GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
            return Ok(duplexResult.Value);
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            GrpcTransportDiagnostics.RecordException(activity, rpcEx, rpcEx.Status.StatusCode, message);
            var error = PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            return Err<IDuplexStreamCall>(error);
        }
        catch (Exception ex)
        {
            GrpcTransportDiagnostics.RecordException(activity, ex, StatusCode.Unknown, ex.Message);
            return PolymerErrors.ToResult<IDuplexStreamCall>(ex, transport: GrpcTransportConstants.TransportName);
        }
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

    private (Uri Address, CallInvoker CallInvoker) GetCallInvoker(RequestMeta requestMeta)
    {
        if (!_started)
        {
            throw new InvalidOperationException("gRPC outbound has not been started.");
        }

        var address = _addresses.Count == 1
            ? _addresses[0]
            : _peerChooser.ChoosePeer(requestMeta, _addresses);

        if (!_callInvokers.TryGetValue(address, out var callInvoker))
        {
            throw new InvalidOperationException($"No gRPC call invoker is registered for peer '{address}'.");
        }

        return (address, callInvoker);
    }

    private CallOptions CreateCallOptions(RequestMeta meta, CancellationToken cancellationToken)
    {
        var metadata = GrpcMetadataAdapter.CreateRequestMetadata(meta);
        var deadline = ResolveDeadline(meta);
        return deadline.HasValue
            ? new CallOptions(metadata, deadline.Value, cancellationToken)
            : new CallOptions(metadata, cancellationToken: cancellationToken);
    }

    private static DateTime? ResolveDeadline(RequestMeta meta)
    {
        if (meta is null)
        {
            throw new ArgumentNullException(nameof(meta));
        }

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
