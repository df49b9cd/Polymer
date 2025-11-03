using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using Hugo;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Transport.Grpc;

internal sealed class GrpcClientStreamCall : IStreamCall
{
    private readonly AsyncServerStreamingCall<byte[]> _call;
    private readonly Channel<ReadOnlyMemory<byte>> _responses;
    private readonly Channel<ReadOnlyMemory<byte>> _requests;
    private readonly CancellationTokenSource _cts;
    private readonly StreamCallContext _context;
    private readonly KeyValuePair<string, object?>[] _baseTags;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private long _responseCount;
    private int _metricsRecorded;

    private GrpcClientStreamCall(
        RequestMeta requestMeta,
        AsyncServerStreamingCall<byte[]> call,
        ResponseMeta responseMeta,
        CancellationToken cancellationToken)
    {
        RequestMeta = requestMeta;
        _call = call;
        ResponseMeta = responseMeta;
        _baseTags = GrpcTransportMetrics.CreateBaseTags(requestMeta);
        _context = new StreamCallContext(StreamDirection.Server);

        _requests = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        _requests.Writer.TryComplete();

        _responses = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = PumpResponsesAsync(_cts.Token);
    }

    public static async ValueTask<Result<GrpcClientStreamCall>> CreateAsync(
        RequestMeta requestMeta,
        AsyncServerStreamingCall<byte[]> call,
        CancellationToken cancellationToken)
    {
        try
        {
            var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var responseMeta = GrpcMetadataAdapter.CreateResponseMeta(headers, null, GrpcTransportConstants.TransportName);
            return Ok(new GrpcClientStreamCall(requestMeta, call, responseMeta, cancellationToken));
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            return Err<GrpcClientStreamCall>(PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName));
        }
        catch (Exception ex)
        {
            return PolymerErrors.ToResult<GrpcClientStreamCall>(ex, transport: GrpcTransportConstants.TransportName);
        }
    }

    public StreamDirection Direction => StreamDirection.Server;

    public RequestMeta RequestMeta { get; }

    public ResponseMeta ResponseMeta { get; private set; }

    public StreamCallContext Context => _context;

    public ChannelWriter<ReadOnlyMemory<byte>> Requests => _requests.Writer;

    public ChannelReader<ReadOnlyMemory<byte>> Responses => _responses.Reader;

    public ValueTask CompleteAsync(Error? error = null, CancellationToken cancellationToken = default)
    {
        _cts.Cancel();
        _responses.Writer.TryComplete();
        var completionStatus = ResolveCompletionStatus(error);
        _context.TrySetCompletion(completionStatus, error);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _call.Dispose();
        _responses.Writer.TryComplete();
        _requests.Writer.TryComplete();
        RecordCompletion(StatusCode.Cancelled);
        _context.TrySetCompletion(StreamCompletionStatus.Cancelled);
    }

    private async Task PumpResponsesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in _call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _responseCount);
                GrpcTransportMetrics.ClientServerStreamResponseMessages.Add(1, _baseTags);
                await _responses.Writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                _context.IncrementMessageCount();
            }

            var trailers = _call.GetTrailers();
            ResponseMeta = GrpcMetadataAdapter.CreateResponseMeta(null, trailers, GrpcTransportConstants.TransportName);
            _responses.Writer.TryComplete();
            RecordCompletion(StatusCode.OK);
            _context.TrySetCompletion(StreamCompletionStatus.Succeeded);
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            var error = PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            _responses.Writer.TryComplete(PolymerErrors.FromError(error, GrpcTransportConstants.TransportName));
            RecordCompletion(rpcEx.Status.StatusCode);
            var completionStatus = status == PolymerStatusCode.Cancelled
                ? StreamCompletionStatus.Cancelled
                : StreamCompletionStatus.Faulted;
            _context.TrySetCompletion(completionStatus, error);
        }
        catch (Exception ex)
        {
            _responses.Writer.TryComplete(ex);
            RecordCompletion(StatusCode.Unknown);
            _context.TrySetCompletion(StreamCompletionStatus.Faulted, PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Internal,
                ex.Message ?? "An unknown error occurred while reading the response stream.",
                transport: GrpcTransportConstants.TransportName,
                inner: Error.FromException(ex)));
        }
    }

    private static StreamCompletionStatus ResolveCompletionStatus(Error? error)
    {
        if (error is null)
        {
            return StreamCompletionStatus.Succeeded;
        }

        return PolymerErrorAdapter.ToStatus(error) switch
        {
            PolymerStatusCode.Cancelled => StreamCompletionStatus.Cancelled,
            _ => StreamCompletionStatus.Faulted
        };
    }

    private void RecordCompletion(StatusCode statusCode)
    {
        if (Interlocked.Exchange(ref _metricsRecorded, 1) == 1)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        var tags = GrpcTransportMetrics.AppendStatus(_baseTags, statusCode);
        GrpcTransportMetrics.ClientServerStreamDuration.Record(elapsed, tags);
        GrpcTransportMetrics.ClientServerStreamResponseCount.Record(_responseCount, tags);
    }
}
