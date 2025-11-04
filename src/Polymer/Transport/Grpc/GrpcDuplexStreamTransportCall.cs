using System.Diagnostics;
using System.Threading.Channels;
using Grpc.Core;
using Hugo;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Transport.Grpc;

internal sealed class GrpcDuplexStreamTransportCall : IDuplexStreamCall
{
    private readonly AsyncDuplexStreamingCall<byte[], byte[]> _call;
    private readonly DuplexStreamCall _inner;
    private readonly CancellationTokenSource _cts;
    private readonly Task _requestPump;
    private readonly Task _responsePump;
    private readonly KeyValuePair<string, object?>[] _baseTags;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private long _requestCount;
    private long _responseCount;
    private int _metricsRecorded;

    private GrpcDuplexStreamTransportCall(
        RequestMeta requestMeta,
        AsyncDuplexStreamingCall<byte[], byte[]> call,
        ResponseMeta responseMeta,
        CancellationToken cancellationToken)
    {
        _call = call;
        _inner = DuplexStreamCall.Create(requestMeta, responseMeta);
        _baseTags = GrpcTransportMetrics.CreateBaseTags(requestMeta);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _requestPump = PumpRequestsAsync(_cts.Token);
        _responsePump = PumpResponsesAsync(_cts.Token);
    }

    public static async ValueTask<Result<IDuplexStreamCall>> CreateAsync(
        RequestMeta requestMeta,
        AsyncDuplexStreamingCall<byte[], byte[]> call,
        CancellationToken cancellationToken)
    {
        try
        {
            var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var responseMeta = GrpcMetadataAdapter.CreateResponseMeta(headers, null, GrpcTransportConstants.TransportName);
            var transportCall = new GrpcDuplexStreamTransportCall(requestMeta, call, responseMeta, cancellationToken);
            return Ok((IDuplexStreamCall)transportCall);
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            return Err<IDuplexStreamCall>(PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName));
        }
        catch (Exception ex)
        {
            return PolymerErrors.ToResult<IDuplexStreamCall>(ex, transport: GrpcTransportConstants.TransportName);
        }
    }

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
        _cts.Cancel();

        try
        {
            await Task.WhenAll(_requestPump, _responsePump).ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }
        finally
        {
            _call.Dispose();
            await _inner.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
            RecordCompletion(StatusCode.Cancelled);
        }
    }

    private async Task PumpRequestsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in _inner.RequestReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _requestCount);
                GrpcTransportMetrics.ClientDuplexRequestMessages.Add(1, _baseTags);
                await _call.RequestStream.WriteAsync(payload.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (RpcException rpcEx)
        {
            var error = MapRpcException(rpcEx);
            await _inner.CompleteResponsesAsync(error, cancellationToken).ConfigureAwait(false);
            RecordCompletion(rpcEx.Status.StatusCode);
        }
        catch (Exception ex)
        {
            await _inner.CompleteResponsesAsync(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Internal,
                ex.Message ?? "An error occurred while sending request stream.",
                transport: GrpcTransportConstants.TransportName,
                inner: Error.FromException(ex)), cancellationToken).ConfigureAwait(false);
            RecordCompletion(StatusCode.Unknown);
        }
    }

    private async Task PumpResponsesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in _call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _responseCount);
                GrpcTransportMetrics.ClientDuplexResponseMessages.Add(1, _baseTags);
                await _inner.ResponseWriter.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            var trailers = _call.GetTrailers();
            _inner.SetResponseMeta(GrpcMetadataAdapter.CreateResponseMeta(null, trailers, GrpcTransportConstants.TransportName));
            await _inner.CompleteResponsesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            RecordCompletion(StatusCode.OK);
        }
        catch (RpcException rpcEx)
        {
            var error = MapRpcException(rpcEx);
            await _inner.CompleteResponsesAsync(error, cancellationToken).ConfigureAwait(false);
            RecordCompletion(rpcEx.Status.StatusCode);
        }
        catch (Exception ex)
        {
            await _inner.CompleteResponsesAsync(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Internal,
                ex.Message ?? "An error occurred while reading response stream.",
                transport: GrpcTransportConstants.TransportName,
                inner: Error.FromException(ex)), cancellationToken).ConfigureAwait(false);
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
        GrpcTransportMetrics.ClientDuplexDuration.Record(elapsed, tags);
        GrpcTransportMetrics.ClientDuplexRequestCount.Record(_requestCount, tags);
        GrpcTransportMetrics.ClientDuplexResponseCount.Record(_responseCount, tags);
    }
}
