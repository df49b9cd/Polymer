using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Errors;
using Hugo;
using static Hugo.Go;

namespace Polymer.Transport.Grpc;

public sealed class GrpcOutbound : IUnaryOutbound
{
    private readonly Uri _address;
    private readonly GrpcChannelOptions _channelOptions;
    private GrpcChannel? _channel;
    private CallInvoker? _callInvoker;

    public GrpcOutbound(Uri address, GrpcChannelOptions? options = null)
    {
        _address = address ?? throw new ArgumentNullException(nameof(address));
        _channelOptions = options ?? new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
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

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
            _channel = null;
            _callInvoker = null;
        }
    }

    private async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallUnaryAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        if (_callInvoker is null)
        {
            throw new InvalidOperationException("gRPC outbound has not been started.");
        }

        var metadata = BuildMetadata(request.Meta);
        var callOptions = new CallOptions(metadata, cancellationToken: cancellationToken);

        try
        {
            var method = new Method<byte[], byte[]>(
                MethodType.Unary,
                GrpcTransportConstants.ServiceName,
                GrpcTransportConstants.UnaryMethod,
                DispatcherGrpcService.ByteMarshaller,
                DispatcherGrpcService.ByteMarshaller);

            var call = _callInvoker.AsyncUnaryCall(method, null, callOptions, request.Body.ToArray());
            var response = await call.ResponseAsync.ConfigureAwait(false);
            var responseHeaders = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var trailers = call.GetTrailers();

            var headerPairs = responseHeaders
                .Concat(trailers)
                .Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value));

            var encoding = trailers.FirstOrDefault(entry => entry.Key == GrpcTransportConstants.MetadataEncoding)?.Value;

            var responseMeta = new ResponseMeta(
                encoding: encoding,
                transport: "grpc",
                headers: headerPairs);

            return Go.Ok(Response<ReadOnlyMemory<byte>>.Create(response, responseMeta));
        }
        catch (RpcException rpcException)
        {
            var status = GrpcStatusMapper.FromStatus(rpcException.Status);
            var error = PolymerErrorAdapter.FromStatus(status, rpcException.Status.Detail, transport: "grpc");
            return Go.Err<Response<ReadOnlyMemory<byte>>>(error);
        }
        catch (Exception ex)
        {
            return PolymerErrors.ToResult<Response<ReadOnlyMemory<byte>>>(ex, transport: "grpc");
        }
    }

    private static Metadata BuildMetadata(RequestMeta meta)
    {
        var metadata = new Metadata
        {
            { GrpcTransportConstants.MetadataProcedure, meta.Procedure ?? string.Empty }
        };

        if (!string.IsNullOrEmpty(meta.Encoding)) metadata.Add(GrpcTransportConstants.MetadataEncoding, meta.Encoding);
        if (!string.IsNullOrEmpty(meta.Caller)) metadata.Add(GrpcTransportConstants.MetadataCaller, meta.Caller);
        if (!string.IsNullOrEmpty(meta.ShardKey)) metadata.Add(GrpcTransportConstants.MetadataShardKey, meta.ShardKey);
        if (!string.IsNullOrEmpty(meta.RoutingKey)) metadata.Add(GrpcTransportConstants.MetadataRoutingKey, meta.RoutingKey);
        if (!string.IsNullOrEmpty(meta.RoutingDelegate)) metadata.Add(GrpcTransportConstants.MetadataRoutingDelegate, meta.RoutingDelegate);

        if (meta.TimeToLive is { } ttl)
        {
            metadata.Add(GrpcTransportConstants.MetadataTtl, ttl.TotalMilliseconds.ToString(CultureInfo.InvariantCulture));
        }

        if (meta.Deadline is { } deadline)
        {
            metadata.Add(GrpcTransportConstants.MetadataDeadline, deadline.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }

        foreach (var header in meta.Headers)
        {
            metadata.Add(header.Key.ToLowerInvariant(), header.Value);
        }

        return metadata;
    }

    async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> IUnaryOutbound.CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken) =>
        await CallUnaryAsync(request, cancellationToken).ConfigureAwait(false);
}
