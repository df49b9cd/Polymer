using System.Diagnostics;
using Grpc.Core;
using Hugo;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Transport.Grpc;

internal sealed class GrpcClientStreamTransportCall : IClientStreamTransportCall
{
    private readonly AsyncClientStreamingCall<byte[], byte[]> _call;
    private readonly WriteOptions? _writeOptions;
    private readonly KeyValuePair<string, object?>[] _baseTags;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private long _requestCount;
    private int _metricsRecorded;
    private readonly TaskCompletionSource<Result<Response<ReadOnlyMemory<byte>>>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completed;
    private bool _disposed;

    public GrpcClientStreamTransportCall(
        RequestMeta requestMeta,
        AsyncClientStreamingCall<byte[], byte[]> call,
        WriteOptions? writeOptions)
    {
        RequestMeta = requestMeta ?? throw new ArgumentNullException(nameof(requestMeta));
        _call = call ?? throw new ArgumentNullException(nameof(call));
        _writeOptions = writeOptions;
        ResponseMeta = new ResponseMeta();
        _baseTags = GrpcTransportMetrics.CreateBaseTags(requestMeta);

        _ = ObserveResponseAsync();
    }

    public RequestMeta RequestMeta { get; }

    public ResponseMeta ResponseMeta { get; private set; }

    public Task<Result<Response<ReadOnlyMemory<byte>>>> Response => _completion.Task;

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GrpcClientStreamTransportCall));
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (_writeOptions is not null)
            {
                _call.RequestStream.WriteOptions = _writeOptions;
            }

            await _call.RequestStream.WriteAsync(payload.ToArray(), cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _requestCount);
            GrpcTransportMetrics.ClientClientStreamRequestMessages.Add(1, _baseTags);
        }
        catch (RpcException rpcEx)
        {
            var error = MapRpcException(rpcEx);
            _completion.TrySetResult(Err<Response<ReadOnlyMemory<byte>>>(error));
            RecordCompletion(rpcEx.Status.StatusCode);
            throw PolymerErrors.FromError(error, GrpcTransportConstants.TransportName);
        }
        catch (Exception ex)
        {
            var error = PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Internal,
                ex.Message ?? "An error occurred while writing to the client stream.",
                transport: GrpcTransportConstants.TransportName,
                inner: Error.FromException(ex));
            _completion.TrySetResult(Err<Response<ReadOnlyMemory<byte>>>(error));
            RecordCompletion(StatusCode.Unknown);
            throw PolymerErrors.FromError(error, GrpcTransportConstants.TransportName);
        }
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || Interlocked.Exchange(ref _completed, 1) == 1)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (RpcException rpcEx)
        {
            var error = MapRpcException(rpcEx);
            _completion.TrySetResult(Err<Response<ReadOnlyMemory<byte>>>(error));
            RecordCompletion(rpcEx.Status.StatusCode);
            throw PolymerErrors.FromError(error, GrpcTransportConstants.TransportName);
        }
        catch (Exception ex)
        {
            var error = PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Internal,
                ex.Message ?? "An error occurred while completing the client stream.",
                transport: GrpcTransportConstants.TransportName,
                inner: Error.FromException(ex));
            _completion.TrySetResult(Err<Response<ReadOnlyMemory<byte>>>(error));
            RecordCompletion(StatusCode.Unknown);
            throw PolymerErrors.FromError(error, GrpcTransportConstants.TransportName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (Volatile.Read(ref _completed) == 0)
            {
                try
                {
                    await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }
        }
        finally
        {
            _call.Dispose();
            RecordCompletion(StatusCode.Cancelled);
        }
    }

    private async Task ObserveResponseAsync()
    {
        try
        {
            var headers = await _call.ResponseHeadersAsync.ConfigureAwait(false);
            var payload = await _call.ResponseAsync.ConfigureAwait(false);
            var trailers = _call.GetTrailers();

            ResponseMeta = GrpcMetadataAdapter.CreateResponseMeta(headers, trailers, GrpcTransportConstants.TransportName);
            var response = Response<ReadOnlyMemory<byte>>.Create(payload, ResponseMeta);
            _completion.TrySetResult(Ok(response));
            RecordCompletion(StatusCode.OK);
        }
        catch (RpcException rpcEx)
        {
            var error = MapRpcException(rpcEx);
            _completion.TrySetResult(Err<Response<ReadOnlyMemory<byte>>>(error));
            RecordCompletion(rpcEx.Status.StatusCode);
        }
        catch (Exception ex)
        {
            var error = PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Internal,
                ex.Message ?? "An error occurred while reading the client stream response.",
                transport: GrpcTransportConstants.TransportName,
                inner: Error.FromException(ex));
            _completion.TrySetResult(Err<Response<ReadOnlyMemory<byte>>>(error));
            RecordCompletion(StatusCode.Unknown);
        }
    }

    private static Error MapRpcException(RpcException rpcException)
    {
        var status = GrpcStatusMapper.FromStatus(rpcException.Status);
        var message = string.IsNullOrWhiteSpace(rpcException.Status.Detail)
            ? rpcException.Status.StatusCode.ToString()
            : rpcException.Status.Detail;
        return PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
    }

    private void RecordCompletion(StatusCode statusCode)
    {
        if (Interlocked.Exchange(ref _metricsRecorded, 1) == 1)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        var tags = GrpcTransportMetrics.AppendStatus(_baseTags, statusCode);
        GrpcTransportMetrics.ClientClientStreamDuration.Record(elapsed, tags);
        GrpcTransportMetrics.ClientClientStreamRequestCount.Record(_requestCount, tags);
    }
}
