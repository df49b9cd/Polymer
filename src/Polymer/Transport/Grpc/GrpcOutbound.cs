using System;
using System.Collections.Concurrent;
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
    private readonly Uri _address;
    private readonly string _remoteService;
    private readonly GrpcChannelOptions _channelOptions;
    private GrpcChannel? _channel;
    private CallInvoker? _callInvoker;
    private readonly ConcurrentDictionary<string, Method<byte[], byte[]>> _unaryMethods = new();
    private readonly ConcurrentDictionary<string, Method<byte[], byte[]>> _serverStreamMethods = new();
    private readonly ConcurrentDictionary<string, Method<byte[], byte[]>> _clientStreamMethods = new();
    private readonly ConcurrentDictionary<string, Method<byte[], byte[]>> _duplexMethods = new();

    public GrpcOutbound(Uri address, string remoteService, GrpcChannelOptions? channelOptions = null)
    {
        _address = address ?? throw new ArgumentNullException(nameof(address));
        _remoteService = string.IsNullOrWhiteSpace(remoteService)
            ? throw new ArgumentException("Remote service name must be provided.", nameof(remoteService))
            : remoteService;
        _channelOptions = channelOptions ?? new GrpcChannelOptions
        {
            HttpHandler = new System.Net.Http.SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true
            }
        };
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        _channel = GrpcChannel.ForAddress(_address, _channelOptions);
        _callInvoker = _channel.CreateCallInvoker();
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _channel?.Dispose();
        _channel = null;
        _callInvoker = null;
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
        if (_callInvoker is null)
        {
            throw new InvalidOperationException("gRPC outbound has not been started.");
        }

        if (string.IsNullOrEmpty(request.Meta.Procedure))
        {
            return Err<Response<ReadOnlyMemory<byte>>>(
                PolymerErrorAdapter.FromStatus(PolymerStatusCode.InvalidArgument, "Procedure metadata is required for gRPC calls.", transport: GrpcTransportConstants.TransportName));
        }

        var method = _unaryMethods.GetOrAdd(request.Meta.Procedure, CreateUnaryMethod);
        var metadata = GrpcMetadataAdapter.CreateRequestMetadata(request.Meta);
        var callOptions = new CallOptions(metadata, cancellationToken: cancellationToken);

        try
        {
            var call = _callInvoker.AsyncUnaryCall(method, null, callOptions, request.Body.ToArray());
            var response = await call.ResponseAsync.ConfigureAwait(false);

            var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var trailers = call.GetTrailers();
            var responseMeta = GrpcMetadataAdapter.CreateResponseMeta(headers, trailers, GrpcTransportConstants.TransportName);

            return Ok(Response<ReadOnlyMemory<byte>>.Create(response, responseMeta));
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            var error = PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            return Err<Response<ReadOnlyMemory<byte>>>(error);
        }
        catch (Exception ex)
        {
            return PolymerErrors.ToResult<Response<ReadOnlyMemory<byte>>>(ex, transport: GrpcTransportConstants.TransportName);
        }
    }

    async ValueTask<Result<OnewayAck>> IOnewayOutbound.CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken)
    {
        if (_callInvoker is null)
        {
            throw new InvalidOperationException("gRPC outbound has not been started.");
        }

        if (string.IsNullOrEmpty(request.Meta.Procedure))
        {
            return Err<OnewayAck>(
                PolymerErrorAdapter.FromStatus(PolymerStatusCode.InvalidArgument, "Procedure metadata is required for gRPC calls.", transport: GrpcTransportConstants.TransportName));
        }

        var method = _unaryMethods.GetOrAdd(request.Meta.Procedure, CreateUnaryMethod);
        var metadata = GrpcMetadataAdapter.CreateRequestMetadata(request.Meta);
        var callOptions = new CallOptions(metadata, cancellationToken: cancellationToken);

        try
        {
            var call = _callInvoker.AsyncUnaryCall(method, null, callOptions, request.Body.ToArray());
            await call.ResponseAsync.ConfigureAwait(false);

            var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var trailers = call.GetTrailers();
            var responseMeta = GrpcMetadataAdapter.CreateResponseMeta(headers, trailers, GrpcTransportConstants.TransportName);

            return Ok(OnewayAck.Ack(responseMeta));
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            var error = PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            return Err<OnewayAck>(error);
        }
        catch (Exception ex)
        {
            return PolymerErrors.ToResult<OnewayAck>(ex, transport: GrpcTransportConstants.TransportName);
        }
    }

    public async ValueTask<Result<IStreamCall>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_callInvoker is null)
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

        var method = _serverStreamMethods.GetOrAdd(request.Meta.Procedure, CreateServerStreamingMethod);
        var metadata = GrpcMetadataAdapter.CreateRequestMetadata(request.Meta);
        var callOptions = new CallOptions(metadata, cancellationToken: cancellationToken);

        try
        {
            var call = _callInvoker.AsyncServerStreamingCall(method, null, callOptions, request.Body.ToArray());
            var streamCallResult = await GrpcClientStreamCall.CreateAsync(request.Meta, call, cancellationToken).ConfigureAwait(false);
            if (streamCallResult.IsFailure)
            {
                call.Dispose();
                return Err<IStreamCall>(streamCallResult.Error!);
            }

            return Ok((IStreamCall)streamCallResult.Value);
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            var error = PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            return Err<IStreamCall>(error);
        }
        catch (Exception ex)
        {
            return PolymerErrors.ToResult<IStreamCall>(ex, transport: GrpcTransportConstants.TransportName);
        }
    }

    public ValueTask<Result<IClientStreamTransportCall>> CallAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken = default)
    {
        if (_callInvoker is null)
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

        var method = _clientStreamMethods.GetOrAdd(requestMeta.Procedure, CreateClientStreamingMethod);
        var metadata = GrpcMetadataAdapter.CreateRequestMetadata(requestMeta);
        var callOptions = new CallOptions(metadata, cancellationToken: cancellationToken);

        try
        {
            var call = _callInvoker.AsyncClientStreamingCall(method, null, callOptions);
            var streamingCall = new GrpcClientStreamTransportCall(requestMeta, call, null);
            return ValueTask.FromResult(Ok((IClientStreamTransportCall)streamingCall));
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            var error = PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            return ValueTask.FromResult(Err<IClientStreamTransportCall>(error));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(PolymerErrors.ToResult<IClientStreamTransportCall>(ex, transport: GrpcTransportConstants.TransportName));
        }
    }

    async ValueTask<Result<IDuplexStreamCall>> IDuplexOutbound.CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken)
    {
        if (_callInvoker is null)
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

        var method = _duplexMethods.GetOrAdd(request.Meta.Procedure, CreateDuplexStreamingMethod);
        var metadata = GrpcMetadataAdapter.CreateRequestMetadata(request.Meta);
        var callOptions = new CallOptions(metadata, cancellationToken: cancellationToken);

        try
        {
            var call = _callInvoker.AsyncDuplexStreamingCall(method, null, callOptions);
            var duplexResult = await GrpcDuplexStreamTransportCall.CreateAsync(request.Meta, call, cancellationToken).ConfigureAwait(false);
            if (duplexResult.IsFailure)
            {
                call.Dispose();
                return Err<IDuplexStreamCall>(duplexResult.Error!);
            }

            return Ok(duplexResult.Value);
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            var error = PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            return Err<IDuplexStreamCall>(error);
        }
        catch (Exception ex)
        {
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
}
