using System.Threading.Channels;
using Hugo;
using OmniRelay.Errors;

namespace OmniRelay.Core.Transport;

/// <summary>
/// Duplex streaming call that provides independent request and response streams
/// and tracks message counts for metrics.
/// </summary>
public sealed class DuplexStreamCall : IDuplexStreamCall
{
    private readonly Channel<ReadOnlyMemory<byte>> _requests;
    private readonly Channel<ReadOnlyMemory<byte>> _responses;
    private bool _requestsCompleted;
    private bool _responsesCompleted;

    private DuplexStreamCall(RequestMeta requestMeta, ResponseMeta responseMeta)
    {
        RequestMeta = requestMeta ?? throw new ArgumentNullException(nameof(requestMeta));
        ResponseMeta = responseMeta ?? new ResponseMeta();
        Context = new DuplexStreamCallContext();

        _requests = Go.MakeChannel<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });

        RequestWriter = new CountingChannelWriter(
            _requests.Writer,
            () => Context.IncrementRequestMessageCount());

        _responses = Go.MakeChannel<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });

        ResponseWriter = new CountingChannelWriter(
            _responses.Writer,
            () => Context.IncrementResponseMessageCount());
    }

    /// <summary>
    /// Creates a duplex streaming call instance.
    /// </summary>
    public static DuplexStreamCall Create(RequestMeta requestMeta, ResponseMeta? responseMeta = null) =>
        new(requestMeta, responseMeta ?? new ResponseMeta());

    /// <inheritdoc />
    public RequestMeta RequestMeta { get; }

    /// <inheritdoc />
    public ResponseMeta ResponseMeta { get; private set; }

    /// <inheritdoc />
    public DuplexStreamCallContext Context { get; }

    /// <inheritdoc />
    public ChannelWriter<ReadOnlyMemory<byte>> RequestWriter { get; }

    /// <inheritdoc />
    public ChannelReader<ReadOnlyMemory<byte>> RequestReader => _requests.Reader;

    /// <inheritdoc />
    public ChannelWriter<ReadOnlyMemory<byte>> ResponseWriter { get; }

    /// <inheritdoc />
    public ChannelReader<ReadOnlyMemory<byte>> ResponseReader => _responses.Reader;

    /// <summary>Updates the response metadata.</summary>
    public void SetResponseMeta(ResponseMeta meta) => ResponseMeta = meta ?? new ResponseMeta();

    /// <inheritdoc />
    public ValueTask CompleteRequestsAsync(Error? error = null, CancellationToken cancellationToken = default)
    {
        if (_requestsCompleted)
        {
            return ValueTask.CompletedTask;
        }

        _requestsCompleted = true;
        TryCompleteChannel(_requests.Writer, error, RequestMeta.Transport);
        var status = ResolveCompletionStatus(error);
        Context.TrySetRequestCompletion(status, error);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask CompleteResponsesAsync(Error? error = null, CancellationToken cancellationToken = default)
    {
        if (_responsesCompleted)
        {
            return ValueTask.CompletedTask;
        }

        _responsesCompleted = true;
        TryCompleteChannel(_responses.Writer, error, RequestMeta.Transport);
        var status = ResolveCompletionStatus(error);
        Context.TrySetResponseCompletion(status, error);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _requests.Writer.TryComplete();
        _responses.Writer.TryComplete();
        Context.TrySetRequestCompletion(StreamCompletionStatus.Cancelled);
        Context.TrySetResponseCompletion(StreamCompletionStatus.Cancelled);
        return ValueTask.CompletedTask;
    }

    private static void TryCompleteChannel(ChannelWriter<ReadOnlyMemory<byte>> writer, Error? error, string? transport)
    {
        if (error is null)
        {
            writer.TryComplete();
            return;
        }

        var exception = OmniRelayErrors.FromError(error, transport);
        writer.TryComplete(exception);
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

    private sealed class CountingChannelWriter(ChannelWriter<ReadOnlyMemory<byte>> inner, Action onWrite) : ChannelWriter<ReadOnlyMemory<byte>>
    {
        private readonly ChannelWriter<ReadOnlyMemory<byte>> _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        private readonly Action _onWrite = onWrite ?? throw new ArgumentNullException(nameof(onWrite));

        public override bool TryWrite(ReadOnlyMemory<byte> item)
        {
            if (_inner.TryWrite(item))
            {
                _onWrite();
                return true;
            }

            return false;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> item, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            _onWrite();
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            _inner.WaitToWriteAsync(cancellationToken);

        public override bool TryComplete(Exception? error = null) =>
            _inner.TryComplete(error);
    }
}
