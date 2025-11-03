using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.AspNetCore.Server.Model;
using Grpc.Core;
using Hugo;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Dispatcher;
using Polymer.Errors;

namespace Polymer.Transport.Grpc;

internal sealed class GrpcDispatcherServiceMethodProvider(Dispatcher.Dispatcher dispatcher, GrpcInbound inbound) : IServiceMethodProvider<GrpcDispatcherService>
{
    private readonly Dispatcher.Dispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private readonly GrpcInbound _inbound = inbound ?? throw new ArgumentNullException(nameof(inbound));

    public void OnServiceMethodDiscovery(ServiceMethodProviderContext<GrpcDispatcherService> context)
    {
        var procedures = _dispatcher.ListProcedures();

        foreach (var spec in procedures.OfType<UnaryProcedureSpec>())
        {
            var method = new Method<byte[], byte[]>(
                MethodType.Unary,
                _dispatcher.ServiceName,
                spec.Name,
                GrpcMarshallerCache.ByteMarshaller,
                GrpcMarshallerCache.ByteMarshaller);

            UnaryServerMethod<GrpcDispatcherService, byte[], byte[]> handler = async (_, request, callContext) =>
            {
                if (!_inbound.TryEnterCall(callContext, out var scope, out var rejection))
                {
                    throw rejection!;
                }

                using var callScope = scope;
                var metadata = callContext.RequestHeaders ?? [];
                var encoding = metadata.GetValue(GrpcTransportConstants.EncodingHeader);

                using var activity = GrpcTransportDiagnostics.StartServerActivity(
                    _dispatcher.ServiceName,
                    spec.Name,
                    metadata,
                    callContext,
                    "unary");

                var requestMeta = GrpcMetadataAdapter.BuildRequestMeta(
                    _dispatcher.ServiceName,
                    spec.Name,
                    metadata,
                    encoding);

                var dispatcherRequest = new Request<ReadOnlyMemory<byte>>(requestMeta, request);
                var result = await _dispatcher.InvokeUnaryAsync(spec.Name, dispatcherRequest, callContext.CancellationToken)
                    .ConfigureAwait(false);

                if (result.IsFailure)
                {
                    var exception = PolymerErrors.FromError(result.Error!, GrpcTransportConstants.TransportName);
                    var status = GrpcStatusMapper.ToStatus(exception.StatusCode, exception.Message);
                    var trailers = GrpcMetadataAdapter.CreateErrorTrailers(exception.Error);
                    GrpcTransportDiagnostics.RecordException(activity, exception, status.StatusCode, exception.Message);
                    throw new RpcException(status, trailers);
                }

                var response = result.Value;
                var headers = GrpcMetadataAdapter.CreateResponseHeaders(response.Meta);
                if (headers.Count > 0)
                {
                    await callContext.WriteResponseHeadersAsync(headers).ConfigureAwait(false);
                }

                GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
                return response.Body.ToArray();
            };

            context.AddUnaryMethod<byte[], byte[]>(method, [], handler);
        }

        foreach (var spec in procedures.OfType<OnewayProcedureSpec>())
        {
            var method = new Method<byte[], byte[]>(
                MethodType.Unary,
                _dispatcher.ServiceName,
                spec.Name,
                GrpcMarshallerCache.ByteMarshaller,
                GrpcMarshallerCache.ByteMarshaller);

            UnaryServerMethod<GrpcDispatcherService, byte[], byte[]> handler = async (_, request, callContext) =>
            {
                if (!_inbound.TryEnterCall(callContext, out var scope, out var rejection))
                {
                    throw rejection!;
                }

                using var callScope = scope;
                var metadata = callContext.RequestHeaders ?? [];
                var encoding = metadata.GetValue(GrpcTransportConstants.EncodingHeader);

                using var activity = GrpcTransportDiagnostics.StartServerActivity(
                    _dispatcher.ServiceName,
                    spec.Name,
                    metadata,
                    callContext,
                    "oneway");

                var requestMeta = GrpcMetadataAdapter.BuildRequestMeta(
                    _dispatcher.ServiceName,
                    spec.Name,
                    metadata,
                    encoding);

                var dispatcherRequest = new Request<ReadOnlyMemory<byte>>(requestMeta, request);
                var result = await _dispatcher.InvokeOnewayAsync(spec.Name, dispatcherRequest, callContext.CancellationToken)
                    .ConfigureAwait(false);

                if (result.IsFailure)
                {
                    var exception = PolymerErrors.FromError(result.Error!, GrpcTransportConstants.TransportName);
                    var status = GrpcStatusMapper.ToStatus(exception.StatusCode, exception.Message);
                    var trailers = GrpcMetadataAdapter.CreateErrorTrailers(exception.Error);
                    GrpcTransportDiagnostics.RecordException(activity, exception, status.StatusCode, exception.Message);
                    throw new RpcException(status, trailers);
                }

                var headers = GrpcMetadataAdapter.CreateResponseHeaders(result.Value.Meta);
                if (headers.Count > 0)
                {
                    await callContext.WriteResponseHeadersAsync(headers).ConfigureAwait(false);
                }

                GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
                return [];
            };

            context.AddUnaryMethod<byte[], byte[]>(method, [], handler);
        }

        foreach (var spec in procedures.OfType<StreamProcedureSpec>())
        {
            var method = new Method<byte[], byte[]>(
                MethodType.ServerStreaming,
                _dispatcher.ServiceName,
                spec.Name,
                GrpcMarshallerCache.ByteMarshaller,
                GrpcMarshallerCache.ByteMarshaller);

            ServerStreamingServerMethod<GrpcDispatcherService, byte[], byte[]> handler = async (_, request, responseStream, callContext) =>
            {
                if (!_inbound.TryEnterCall(callContext, out var scope, out var rejection))
                {
                    throw rejection!;
                }

                using var callScope = scope;
                var metadata = callContext.RequestHeaders ?? [];
                var encoding = metadata.GetValue(GrpcTransportConstants.EncodingHeader);

                using var activity = GrpcTransportDiagnostics.StartServerActivity(
                    _dispatcher.ServiceName,
                    spec.Name,
                    metadata,
                    callContext,
                    "server_stream");

                var requestMeta = GrpcMetadataAdapter.BuildRequestMeta(
                    _dispatcher.ServiceName,
                    spec.Name,
                    metadata,
                    encoding);

                var dispatcherRequest = new Request<ReadOnlyMemory<byte>>(requestMeta, request);
                var streamResult = await _dispatcher.InvokeStreamAsync(
                    spec.Name,
                    dispatcherRequest,
                    new StreamCallOptions(StreamDirection.Server),
                    callContext.CancellationToken).ConfigureAwait(false);

                if (streamResult.IsFailure)
                {
                    var exception = PolymerErrors.FromError(streamResult.Error!, GrpcTransportConstants.TransportName);
                    var status = GrpcStatusMapper.ToStatus(exception.StatusCode, exception.Message);
                    var trailers = GrpcMetadataAdapter.CreateErrorTrailers(exception.Error);
                    var rpcException = new RpcException(status, trailers);
                    GrpcTransportDiagnostics.RecordException(activity, rpcException, status.StatusCode, status.Detail);
                    throw rpcException;
                }

                await using var streamCall = streamResult.Value;
                var cancellationToken = callContext.CancellationToken;
                var headersSent = false;

                async Task EnsureHeadersAsync()
                {
                    if (headersSent)
                    {
                        return;
                    }

                    var headers = GrpcMetadataAdapter.CreateResponseHeaders(streamCall.ResponseMeta);
                    if (headers.Count > 0)
                    {
                        await callContext.WriteResponseHeadersAsync(headers).ConfigureAwait(false);
                    }

                    headersSent = true;
                }

                try
                {
                    await foreach (var payload in streamCall.Responses.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await EnsureHeadersAsync().ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        await responseStream.WriteAsync(payload.ToArray()).ConfigureAwait(false);
                    }

                    await EnsureHeadersAsync().ConfigureAwait(false);
                    ApplySuccessTrailers(callContext, streamCall.ResponseMeta);
                }
                catch (OperationCanceledException)
                {
                    var error = PolymerErrorAdapter.FromStatus(
                        PolymerStatusCode.Cancelled,
                        "The client cancelled the request.",
                        transport: GrpcTransportConstants.TransportName);
                    await streamCall.CompleteAsync(error, cancellationToken).ConfigureAwait(false);

                    var status = GrpcStatusMapper.ToStatus(PolymerStatusCode.Cancelled, "The client cancelled the request.");
                    var trailers = GrpcMetadataAdapter.CreateErrorTrailers(error);
                    var rpcException = new RpcException(status, trailers);
                    GrpcTransportDiagnostics.RecordException(activity, rpcException, status.StatusCode, status.Detail);
                    throw rpcException;
                }
                catch (Exception ex)
                {
                    var polymerException = PolymerErrors.FromException(ex, GrpcTransportConstants.TransportName);
                    await streamCall.CompleteAsync(polymerException.Error, cancellationToken).ConfigureAwait(false);

                    var status = GrpcStatusMapper.ToStatus(polymerException.StatusCode, polymerException.Message);
                    var trailers = GrpcMetadataAdapter.CreateErrorTrailers(polymerException.Error);
                    var rpcException = new RpcException(status, trailers);
                    GrpcTransportDiagnostics.RecordException(activity, rpcException, status.StatusCode, status.Detail);
                    throw rpcException;
                }

                GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
            };

            context.AddServerStreamingMethod<byte[], byte[]>(method, [], handler);
        }

        foreach (var spec in procedures.OfType<ClientStreamProcedureSpec>())
        {
            var method = new Method<byte[], byte[]>(
                MethodType.ClientStreaming,
                _dispatcher.ServiceName,
                spec.Name,
                GrpcMarshallerCache.ByteMarshaller,
                GrpcMarshallerCache.ByteMarshaller);

            ClientStreamingServerMethod<GrpcDispatcherService, byte[], byte[]> handler = async (_, requestStream, callContext) =>
            {
                if (!_inbound.TryEnterCall(callContext, out var scope, out var rejection))
                {
                    throw rejection!;
                }

                using var callScope = scope;
                var metadata = callContext.RequestHeaders ?? [];
                var encoding = metadata.GetValue(GrpcTransportConstants.EncodingHeader);

                using var activity = GrpcTransportDiagnostics.StartServerActivity(
                    _dispatcher.ServiceName,
                    spec.Name,
                    metadata,
                    callContext,
                    "client_stream");

                var requestMeta = GrpcMetadataAdapter.BuildRequestMeta(
                    _dispatcher.ServiceName,
                    spec.Name,
                    metadata,
                    encoding);

                if (callContext.Deadline != DateTime.MaxValue)
                {
                    var deadlineUtc = DateTime.SpecifyKind(callContext.Deadline, DateTimeKind.Utc);
                    requestMeta = requestMeta with { Deadline = new DateTimeOffset(deadlineUtc) };
                }

                var metricTags = GrpcTransportMetrics.CreateBaseTags(requestMeta);
                var startTimestamp = Stopwatch.GetTimestamp();
                long requestCount = 0;
                int metricsRecorded = 0;

                void RecordServerClientStreamMetrics(StatusCode statusCode)
                {
                    if (Interlocked.Exchange(ref metricsRecorded, 1) == 1)
                    {
                        return;
                    }

                    var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                    var tags = GrpcTransportMetrics.AppendStatus(metricTags, statusCode);
                    GrpcTransportMetrics.ServerClientStreamDuration.Record(elapsed, tags);
                    GrpcTransportMetrics.ServerClientStreamRequestCount.Record(requestCount, tags);
                    GrpcTransportMetrics.ServerClientStreamResponseCount.Record(1, tags);
                }

                var callResult = await _dispatcher.InvokeClientStreamAsync(
                    spec.Name,
                    requestMeta,
                    callContext.CancellationToken).ConfigureAwait(false);

                if (callResult.IsFailure)
                {
                    var exception = PolymerErrors.FromError(callResult.Error!, GrpcTransportConstants.TransportName);
                    var status = GrpcStatusMapper.ToStatus(exception.StatusCode, exception.Message);
                    var trailers = GrpcMetadataAdapter.CreateErrorTrailers(exception.Error);
                    var rpcException = new RpcException(status, trailers);
                    GrpcTransportDiagnostics.RecordException(activity, rpcException, status.StatusCode, status.Detail);
                    RecordServerClientStreamMetrics(status.StatusCode);
                    throw rpcException;
                }

                await using var clientStreamCall = callResult.Value;
                var cancellationToken = callContext.CancellationToken;

                try
                {
                    while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
                    {
                        var payload = requestStream.Current;
                        if (payload is null)
                        {
                            continue;
                        }

                        Interlocked.Increment(ref requestCount);
                        GrpcTransportMetrics.ServerClientStreamRequestMessages.Add(1, metricTags);
                        await clientStreamCall.Requests.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    }

                    await clientStreamCall.CompleteWriterAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (RpcException rpcEx)
                {
                    var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
                    var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail)
                        ? rpcEx.Status.StatusCode.ToString()
                        : rpcEx.Status.Detail;
                    GrpcTransportDiagnostics.RecordException(activity, rpcEx, rpcEx.Status.StatusCode, message);
                    var error = PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
                    await clientStreamCall.CompleteWriterAsync(error).ConfigureAwait(false);
                    RecordServerClientStreamMetrics(rpcEx.Status.StatusCode);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    var error = PolymerErrorAdapter.FromStatus(
                        PolymerStatusCode.Cancelled,
                        "The client cancelled the request.",
                        transport: GrpcTransportConstants.TransportName);
                    await clientStreamCall.CompleteWriterAsync(error).ConfigureAwait(false);
                    var status = GrpcStatusMapper.ToStatus(PolymerStatusCode.Cancelled, "The client cancelled the request.");
                    var rpcException = new RpcException(status);
                    GrpcTransportDiagnostics.RecordException(activity, rpcException, status.StatusCode, status.Detail);
                    RecordServerClientStreamMetrics(status.StatusCode);
                    throw rpcException;
                }
                catch (Exception ex)
                {
                    var message = string.IsNullOrWhiteSpace(ex.Message)
                        ? "An error occurred while reading the client stream."
                        : ex.Message;
                    var error = PolymerErrorAdapter.FromStatus(
                        PolymerStatusCode.Internal,
                        message,
                        transport: GrpcTransportConstants.TransportName);
                    await clientStreamCall.CompleteWriterAsync(error).ConfigureAwait(false);
                    var status = GrpcStatusMapper.ToStatus(PolymerStatusCode.Internal, message);
                    var rpcException = new RpcException(status);
                    GrpcTransportDiagnostics.RecordException(activity, rpcException, status.StatusCode, status.Detail);
                    RecordServerClientStreamMetrics(status.StatusCode);
                    throw rpcException;
                }

                var responseResult = await clientStreamCall.Response.ConfigureAwait(false);

                if (responseResult.IsFailure)
                {
                    var exception = PolymerErrors.FromError(responseResult.Error!, GrpcTransportConstants.TransportName);
                    var status = GrpcStatusMapper.ToStatus(exception.StatusCode, exception.Message);
                    var trailers = GrpcMetadataAdapter.CreateErrorTrailers(exception.Error);
                    var rpcException = new RpcException(status, trailers);
                    GrpcTransportDiagnostics.RecordException(activity, rpcException, status.StatusCode, status.Detail);
                    RecordServerClientStreamMetrics(status.StatusCode);
                    throw rpcException;
                }

                var response = responseResult.Value;
                GrpcTransportMetrics.ServerClientStreamResponseMessages.Add(1, metricTags);
                var headers = GrpcMetadataAdapter.CreateResponseHeaders(response.Meta);
                if (headers.Count > 0)
                {
                    await callContext.WriteResponseHeadersAsync(headers).ConfigureAwait(false);
                }

                RecordServerClientStreamMetrics(StatusCode.OK);
                GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
                return response.Body.ToArray();
            };

            context.AddClientStreamingMethod<byte[], byte[]>(method, [], handler);
        }

        foreach (var spec in procedures.OfType<DuplexProcedureSpec>())
        {
            var method = new Method<byte[], byte[]>(
                MethodType.DuplexStreaming,
                _dispatcher.ServiceName,
                spec.Name,
                GrpcMarshallerCache.ByteMarshaller,
                GrpcMarshallerCache.ByteMarshaller);

            DuplexStreamingServerMethod<GrpcDispatcherService, byte[], byte[]> handler = async (_, requestStream, responseStream, callContext) =>
            {
                if (!_inbound.TryEnterCall(callContext, out var scope, out var rejection))
                {
                    throw rejection!;
                }

                using var callScope = scope;
                var metadata = callContext.RequestHeaders ?? [];
                var encoding = metadata.GetValue(GrpcTransportConstants.EncodingHeader);

                using var activity = GrpcTransportDiagnostics.StartServerActivity(
                    _dispatcher.ServiceName,
                    spec.Name,
                    metadata,
                    callContext,
                    "bidi_stream");
                var activityHasError = false;

                var requestMeta = GrpcMetadataAdapter.BuildRequestMeta(
                    _dispatcher.ServiceName,
                    spec.Name,
                    metadata,
                    encoding);

                if (callContext.Deadline != DateTime.MaxValue)
                {
                    var deadlineUtc = DateTime.SpecifyKind(callContext.Deadline, DateTimeKind.Utc);
                    requestMeta = requestMeta with { Deadline = new DateTimeOffset(deadlineUtc) };
                }

                var metricTags = GrpcTransportMetrics.CreateBaseTags(requestMeta);
                var startTimestamp = Stopwatch.GetTimestamp();
                long requestCount = 0;
                long responseCount = 0;
                int metricsRecorded = 0;

                void RecordServerDuplexMetrics(StatusCode statusCode)
                {
                    if (Interlocked.Exchange(ref metricsRecorded, 1) == 1)
                    {
                        return;
                    }

                    var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                    var tags = GrpcTransportMetrics.AppendStatus(metricTags, statusCode);
                    GrpcTransportMetrics.ServerDuplexDuration.Record(elapsed, tags);
                    GrpcTransportMetrics.ServerDuplexRequestCount.Record(requestCount, tags);
                    GrpcTransportMetrics.ServerDuplexResponseCount.Record(responseCount, tags);
                }

                var dispatcherRequest = new Request<ReadOnlyMemory<byte>>(requestMeta, ReadOnlyMemory<byte>.Empty);
                var callResult = await _dispatcher.InvokeDuplexAsync(spec.Name, dispatcherRequest, callContext.CancellationToken).ConfigureAwait(false);

                if (callResult.IsFailure)
                {
                    var exception = PolymerErrors.FromError(callResult.Error!, GrpcTransportConstants.TransportName);
                    var status = GrpcStatusMapper.ToStatus(exception.StatusCode, exception.Message);
                    var trailers = GrpcMetadataAdapter.CreateErrorTrailers(exception.Error);
                    var rpcException = new RpcException(status, trailers);
                    activityHasError = true;
                    GrpcTransportDiagnostics.RecordException(activity, rpcException, status.StatusCode, status.Detail);
                    RecordServerDuplexMetrics(status.StatusCode);
                    throw rpcException;
                }

                await using var duplexCall = callResult.Value;
                var cancellationToken = callContext.CancellationToken;

                var requestPump = PumpRequestsAsync();
                var responsePump = PumpResponsesAsync();

                await Task.WhenAll(requestPump, responsePump).ConfigureAwait(false);

                async Task PumpRequestsAsync()
                {
                    try
                    {
                        await foreach (var payload in requestStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            Interlocked.Increment(ref requestCount);
                            GrpcTransportMetrics.ServerDuplexRequestMessages.Add(1, metricTags);
                            await duplexCall.RequestWriter.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                        }

                        await duplexCall.CompleteRequestsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex)
                    {
                        var error = PolymerErrorAdapter.FromStatus(
                            PolymerStatusCode.Cancelled,
                            "The client cancelled the request.",
                            transport: GrpcTransportConstants.TransportName);
                        await duplexCall.CompleteRequestsAsync(error, cancellationToken).ConfigureAwait(false);
                        activityHasError = true;
                        GrpcTransportDiagnostics.RecordException(activity, ex, StatusCode.Cancelled, ex.Message);
                        RecordServerDuplexMetrics(StatusCode.Cancelled);
                    }
                    catch (RpcException rpcEx)
                    {
                        var error = PolymerErrorAdapter.FromStatus(
                            GrpcStatusMapper.FromStatus(rpcEx.Status),
                            string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail,
                            transport: GrpcTransportConstants.TransportName);
                        await duplexCall.CompleteRequestsAsync(error, cancellationToken).ConfigureAwait(false);
                        activityHasError = true;
                        GrpcTransportDiagnostics.RecordException(activity, rpcEx, rpcEx.Status.StatusCode, rpcEx.Status.Detail);
                        RecordServerDuplexMetrics(rpcEx.Status.StatusCode);
                    }
                    catch (Exception ex)
                    {
                        var error = PolymerErrorAdapter.FromStatus(
                            PolymerStatusCode.Internal,
                            ex.Message ?? "An error occurred while reading the duplex request stream.",
                            transport: GrpcTransportConstants.TransportName,
                            inner: Error.FromException(ex));
                        await duplexCall.CompleteRequestsAsync(error, cancellationToken).ConfigureAwait(false);
                        activityHasError = true;
                        GrpcTransportDiagnostics.RecordException(activity, ex, StatusCode.Internal, ex.Message);
                        RecordServerDuplexMetrics(StatusCode.Internal);
                    }
                }

                async Task PumpResponsesAsync()
                {
                    var headersSent = false;

                    async Task EnsureHeadersAsync()
                    {
                        if (headersSent)
                        {
                            return;
                        }

                        var headers = GrpcMetadataAdapter.CreateResponseHeaders(duplexCall.ResponseMeta);
                        if (headers.Count > 0)
                        {
                            await callContext.WriteResponseHeadersAsync(headers).ConfigureAwait(false);
                        }

                        headersSent = true;
                    }

                    try
                    {
                        await foreach (var payload in duplexCall.ResponseReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                        {
                            await EnsureHeadersAsync().ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            Interlocked.Increment(ref responseCount);
                            GrpcTransportMetrics.ServerDuplexResponseMessages.Add(1, metricTags);
                            await responseStream.WriteAsync(payload.ToArray()).ConfigureAwait(false);
                        }

                        await EnsureHeadersAsync().ConfigureAwait(false);
                        ApplySuccessTrailers(callContext, duplexCall.ResponseMeta);
                        await duplexCall.CompleteResponsesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                        RecordServerDuplexMetrics(StatusCode.OK);
                    }
                    catch (OperationCanceledException)
                    {
                        var error = PolymerErrorAdapter.FromStatus(
                            PolymerStatusCode.Cancelled,
                            "The client cancelled the request.",
                            transport: GrpcTransportConstants.TransportName);

                        await duplexCall.CompleteResponsesAsync(error, cancellationToken).ConfigureAwait(false);

                        var status = GrpcStatusMapper.ToStatus(PolymerStatusCode.Cancelled, "The client cancelled the request.");
                        var trailers = GrpcMetadataAdapter.CreateErrorTrailers(error);
                        var rpcException = new RpcException(status, trailers);
                        activityHasError = true;
                        GrpcTransportDiagnostics.RecordException(activity, rpcException, status.StatusCode, status.Detail);
                        RecordServerDuplexMetrics(status.StatusCode);
                        throw rpcException;
                    }
                    catch (Exception ex)
                    {
                        var polymerException = PolymerErrors.FromException(ex, GrpcTransportConstants.TransportName);
                        await duplexCall.CompleteResponsesAsync(polymerException.Error, cancellationToken).ConfigureAwait(false);

                        var status = GrpcStatusMapper.ToStatus(polymerException.StatusCode, polymerException.Message);
                        var trailers = GrpcMetadataAdapter.CreateErrorTrailers(polymerException.Error);
                        var rpcException = new RpcException(status, trailers);
                        activityHasError = true;
                        GrpcTransportDiagnostics.RecordException(activity, rpcException, status.StatusCode, status.Detail);
                        RecordServerDuplexMetrics(status.StatusCode);
                        throw rpcException;
                    }
                }

                if (!activityHasError)
                {
                    RecordServerDuplexMetrics(StatusCode.OK);
                    GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
                }
            };

            context.AddDuplexStreamingMethod<byte[], byte[]>(method, [], handler);
        }
    }

    private static void ApplySuccessTrailers(ServerCallContext callContext, ResponseMeta responseMeta)
    {
        ArgumentNullException.ThrowIfNull(callContext);

        if (responseMeta is null)
        {
            return;
        }

        var responseTrailers = GrpcMetadataAdapter.CreateResponseHeaders(responseMeta);
        if (responseTrailers.Count > 0)
        {
            foreach (var entry in responseTrailers)
            {
                callContext.ResponseTrailers.Add(entry);
            }
        }

        if (!ContainsStatusTrailer(callContext.ResponseTrailers))
        {
            callContext.ResponseTrailers.Add(GrpcTransportConstants.StatusTrailer, StatusCode.OK.ToString());
        }
    }

    private static bool ContainsStatusTrailer(Metadata metadata)
    {
        foreach (var entry in metadata)
        {
            if (string.Equals(entry.Key, GrpcTransportConstants.StatusTrailer, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
