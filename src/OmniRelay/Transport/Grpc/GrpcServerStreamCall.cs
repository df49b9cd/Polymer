using System.Diagnostics;
using System.Threading.Channels;
using Grpc.Core;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;

namespace OmniRelay.Transport.Grpc;

/// <summary>
/// Server-side stream call used by gRPC inbound handlers to emit response messages.
/// Implements <see cref="IStreamCall"/> for uniform streaming semantics.
/// </summary>
public sealed class GrpcServerStreamCall : IStreamCall
{
    private readonly Channel<ReadOnlyMemory<byte>> _responses;
    private readonly Channel<ReadOnlyMemory<byte>> _requests;
    private readonly KeyValuePair<string, object?>[] _baseTags;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private bool _completed;
    private long _responseCount;
    private int _metricsRecorded;

    private GrpcServerStreamCall(RequestMeta requestMeta, ResponseMeta responseMeta)
    {
        RequestMeta = requestMeta ?? throw new ArgumentNullException(nameof(requestMeta));
        ResponseMeta = responseMeta ?? new ResponseMeta();
        Context = new StreamCallContext(StreamDirection.Server);
        _baseTags = GrpcTransportMetrics.CreateBaseTags(requestMeta);

        _responses = Go.MakeChannel<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _requests = Go.MakeChannel<ReadOnlyMemory<byte>>();
        _requests.Writer.TryComplete();
    }

    /// <summary>
    /// Creates a gRPC server-streaming call wrapper for the given request.
    /// </summary>
    /// <param name="requestMeta">The request metadata.</param>
    /// <param name="responseMeta">Optional initial response metadata.</param>
    /// <returns>A server-streaming call instance.</returns>
    public static GrpcServerStreamCall Create(RequestMeta requestMeta, ResponseMeta? responseMeta = null) =>
        new(requestMeta, responseMeta ?? new ResponseMeta());

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

    /// <summary>
    /// Updates the response metadata sent to the client.
    /// </summary>
    /// <param name="meta">The response metadata.</param>
    public void SetResponseMeta(ResponseMeta meta) => ResponseMeta = meta ?? new ResponseMeta();

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await _responses.Writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _responseCount);
        Context.IncrementMessageCount();
        GrpcTransportMetrics.ServerServerStreamResponseMessages.Add(1, _baseTags);
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(Error? error = null, CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            return ValueTask.CompletedTask;
        }

        _completed = true;

        var completionStatus = ResolveCompletionStatus(error);

        if (error is null)
        {
            _responses.Writer.TryComplete();
            RecordCompletion(StatusCode.OK);
        }
        else
        {
            var exception = OmniRelayErrors.FromError(error, GrpcTransportConstants.TransportName);
            _responses.Writer.TryComplete(exception);
            var status = GrpcStatusMapper.ToStatus(OmniRelayErrorAdapter.ToStatus(error), exception.Message);
            RecordCompletion(status.StatusCode);
        }

        Context.TrySetCompletion(completionStatus, error);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _responses.Writer.TryComplete();
        _requests.Writer.TryComplete();
        RecordCompletion(StatusCode.Cancelled);
        Context.TrySetCompletion(StreamCompletionStatus.Cancelled);
        return ValueTask.CompletedTask;
    }

    private static StreamCompletionStatus ResolveCompletionStatus(Error? error)
    {
        if (error is null)
        {
            return StreamCompletionStatus.Succeeded;
        }

        return OmniRelayErrorAdapter.ToStatus(error) switch
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
        GrpcTransportMetrics.ServerServerStreamDuration.Record(elapsed, tags);
        GrpcTransportMetrics.ServerServerStreamResponseCount.Record(_responseCount, tags);
    }
}
