using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Polymer.Core;
using Polymer.Dispatcher;
using Polymer.Errors;

namespace Polymer.Transport.Grpc;

internal sealed class DispatcherGrpcService : BindableService
{
    internal static readonly Marshaller<byte[]> ByteMarshaller = new(
        payload => payload,
        payload => payload);

    private readonly Method<byte[], byte[]> _unaryMethod;
    private readonly Dispatcher.Dispatcher _dispatcher;

    public DispatcherGrpcService(Dispatcher.Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _unaryMethod = new Method<byte[], byte[]>(
            MethodType.Unary,
            GrpcTransportConstants.ServiceName,
            GrpcTransportConstants.UnaryMethod,
            ByteMarshaller,
            ByteMarshaller);
    }

    public override void BindService(ServiceBinderBase serviceBinder)
    {
        serviceBinder.AddMethod(_unaryMethod, HandleUnaryAsync);
    }

    private async Task<byte[]> HandleUnaryAsync(byte[] requestPayload, ServerCallContext context)
    {
        var metadata = context.RequestHeaders;
        var transport = "grpc";

        var procedure = metadata?.FirstOrDefault(entry => entry.Key.Equals(GrpcTransportConstants.MetadataProcedure, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrEmpty(procedure))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "rpc procedure header missing"));
        }

        var encoding = metadata?.FirstOrDefault(entry => entry.Key.Equals(GrpcTransportConstants.MetadataEncoding, StringComparison.OrdinalIgnoreCase))?.Value;
        var caller = metadata?.FirstOrDefault(entry => entry.Key.Equals(GrpcTransportConstants.MetadataCaller, StringComparison.OrdinalIgnoreCase))?.Value;
        var shardKey = metadata?.FirstOrDefault(entry => entry.Key.Equals(GrpcTransportConstants.MetadataShardKey, StringComparison.OrdinalIgnoreCase))?.Value;
        var routingKey = metadata?.FirstOrDefault(entry => entry.Key.Equals(GrpcTransportConstants.MetadataRoutingKey, StringComparison.OrdinalIgnoreCase))?.Value;
        var routingDelegate = metadata?.FirstOrDefault(entry => entry.Key.Equals(GrpcTransportConstants.MetadataRoutingDelegate, StringComparison.OrdinalIgnoreCase))?.Value;

        TimeSpan? ttl = null;
        var ttlMetadata = metadata?.FirstOrDefault(entry => entry.Key.Equals(GrpcTransportConstants.MetadataTtl, StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrEmpty(ttlMetadata) && long.TryParse(ttlMetadata, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ttlMs))
        {
            ttl = TimeSpan.FromMilliseconds(ttlMs);
        }

        DateTimeOffset? deadline = null;
        var deadlineMetadata = metadata?.FirstOrDefault(entry => entry.Key.Equals(GrpcTransportConstants.MetadataDeadline, StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrEmpty(deadlineMetadata) && DateTimeOffset.TryParse(deadlineMetadata, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDeadline))
        {
            deadline = parsedDeadline;
        }

        var metaHeaders = metadata?.Select(static entry => new KeyValuePair<string, string>(entry.Key, entry.Value)) ?? Array.Empty<KeyValuePair<string, string>>();

        var requestMeta = new RequestMeta(
            _dispatcher.ServiceName,
            procedure,
            caller: caller,
            encoding: encoding,
            transport: transport,
            shardKey: shardKey,
            routingKey: routingKey,
            routingDelegate: routingDelegate,
            timeToLive: ttl,
            deadline: deadline,
            headers: metaHeaders);

        var request = new Request<ReadOnlyMemory<byte>>(requestMeta, requestPayload);
        var result = await _dispatcher.InvokeUnaryAsync(procedure, request, context.CancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            var error = result.Error!;
            var exception = PolymerErrors.FromError(error, transport);
            var status = GrpcStatusMapper.ToStatus(exception.StatusCode, exception.Message);
            throw new RpcException(status);
        }

        var response = result.Value;
        foreach (var header in response.Meta.Headers)
        {
            context.ResponseTrailers.Add(header.Key, header.Value);
        }

        if (!string.IsNullOrEmpty(response.Meta.Encoding))
        {
            context.ResponseTrailers.Add(GrpcTransportConstants.MetadataEncoding, response.Meta.Encoding);
        }

        return response.Body.ToArray();
    }
}
