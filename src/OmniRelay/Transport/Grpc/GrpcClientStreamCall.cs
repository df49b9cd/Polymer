using System.Diagnostics;
using System.Threading.Channels;
using Grpc.Core;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Transport.Grpc;

/// <summary>
/// Client-side wrapper for gRPC server-streaming calls, adapting them to <see cref="IStreamCall"/>.
/// Handles response pumping, metrics, and completion semantics.
/// </summary>
internal sealed class GrpcClientStreamCall : IStreamCall
{
    private readonly AsyncServerStreamingCall<byte[]> _call;
    private readonly Channel<ReadOnlyMemory<byte>> _responses;
    private readonly Channel<ReadOnlyMemory<byte>> _requests;
    private readonly CancellationTokenSource _cts;
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
        Context = new StreamCallContext(StreamDirection.Server);

        _requests = MakeChannel<ReadOnlyMemory<byte>>();
        _requests.Writer.TryComplete();

        _responses = MakeChannel<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = PumpResponsesAsync(_cts.Token);
    }

    /// <summary>
    /// Creates a client stream call wrapper from an active gRPC server-streaming call.
    /// </summary>
    /// <param name="requestMeta">The request metadata.</param>
    /// <param name="call">The active gRPC server-streaming call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created stream call or an error.</returns>
    public static async ValueTask<Result<GrpcClientStreamCall>> CreateAsync(
        RequestMeta requestMeta,
        AsyncServerStreamingCall<byte[]> call,
        CancellationToken cancellationToken)
    {
        try
        {
            var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var responseMeta = GrpcMetadataAdapter.CreateResponseMeta(headers, null);
            return Ok(new GrpcClientStreamCall(requestMeta, call, responseMeta, cancellationToken));
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            return Err<GrpcClientStreamCall>(OmniRelayErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName));
        }
        catch (Exception ex)
        {
            return OmniRelayErrors.ToResult<GrpcClientStreamCall>(ex, transport: GrpcTransportConstants.TransportName);
        }
    }

    /// <inheritdoc />
    public StreamDirection Direction => StreamDirection.Server;

    /// <inheritdoc />
    public RequestMeta RequestMeta { get; }

    /// <inheritdoc />
    public ResponseMeta ResponseMeta { get; private set; }

    /// <inheritdoc />
    public StreamCallContext Context { get; }

    /// <inheritdoc />
    public ChannelWriter<ReadOnlyMemory<byte>> Requests => _requests.Writer;

    /// <inheritdoc />
    public ChannelReader<ReadOnlyMemory<byte>> Responses => _responses.Reader;

    /// <inheritdoc />
    public ValueTask CompleteAsync(Error? fault = null, CancellationToken cancellationToken = default)
    {
        _cts.Cancel();
        _responses.Writer.TryComplete();
        var completionStatus = ResolveCompletionStatus(fault);
        Context.TrySetCompletion(completionStatus, fault);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _call.Dispose();
        _responses.Writer.TryComplete();
        _requests.Writer.TryComplete();
        RecordCompletion(StatusCode.Cancelled);
        Context.TrySetCompletion(StreamCompletionStatus.Cancelled);
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
                Context.IncrementMessageCount();
            }

            var trailers = _call.GetTrailers();
            ResponseMeta = GrpcMetadataAdapter.CreateResponseMeta(null, trailers);
            _responses.Writer.TryComplete();
            RecordCompletion(StatusCode.OK);
            Context.TrySetCompletion(StreamCompletionStatus.Succeeded);
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            var error = OmniRelayErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
            _responses.Writer.TryComplete(OmniRelayErrors.FromError(error, GrpcTransportConstants.TransportName));
            RecordCompletion(rpcEx.Status.StatusCode);
            var completionStatus = status == OmniRelayStatusCode.Cancelled
                ? StreamCompletionStatus.Cancelled
                : StreamCompletionStatus.Faulted;
            Context.TrySetCompletion(completionStatus, error);
        }
        catch (Exception ex)
        {
            _responses.Writer.TryComplete(ex);
            RecordCompletion(StatusCode.Unknown);
            Context.TrySetCompletion(StreamCompletionStatus.Faulted, OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Internal,
                ex.Message ?? "An unknown error occurred while reading the response stream.",
                transport: GrpcTransportConstants.TransportName,
                inner: Error.FromException(ex)));
        }
    }

    private static StreamCompletionStatus ResolveCompletionStatus(Error? fault)
    {
        if (fault is null)
        {
            return StreamCompletionStatus.Succeeded;
        }

        return OmniRelayErrorAdapter.ToStatus(fault) switch
        {
            OmniRelayStatusCode.Cancelled => StreamCompletionStatus.Cancelled,
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
