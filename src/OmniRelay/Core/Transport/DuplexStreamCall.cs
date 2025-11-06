using System.Threading.Channels;
using Hugo;
using OmniRelay.Errors;

namespace OmniRelay.Core.Transport;

public sealed class DuplexStreamCall : IDuplexStreamCall
{
    private readonly Channel<ReadOnlyMemory<byte>> _requests;
    private readonly Channel<ReadOnlyMemory<byte>> _responses;
    private readonly ChannelWriter<ReadOnlyMemory<byte>> _requestWriter;
    private readonly ChannelWriter<ReadOnlyMemory<byte>> _responseWriter;
    private readonly DuplexStreamCallContext _context;
    private bool _requestsCompleted;
    private bool _responsesCompleted;

    private DuplexStreamCall(RequestMeta requestMeta, ResponseMeta responseMeta)
    {
        RequestMeta = requestMeta ?? throw new ArgumentNullException(nameof(requestMeta));
        ResponseMeta = responseMeta ?? new ResponseMeta();
        _context = new DuplexStreamCallContext();

        _requests = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });

        _requestWriter = new CountingChannelWriter(
            _requests.Writer,
            () => _context.IncrementRequestMessageCount());

        _responses = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });

        _responseWriter = new CountingChannelWriter(
            _responses.Writer,
            () => _context.IncrementResponseMessageCount());
    }

    public static DuplexStreamCall Create(RequestMeta requestMeta, ResponseMeta? responseMeta = null) =>
        new(requestMeta, responseMeta ?? new ResponseMeta());

    public RequestMeta RequestMeta { get; }

    public ResponseMeta ResponseMeta { get; private set; }

    public DuplexStreamCallContext Context => _context;

    public ChannelWriter<ReadOnlyMemory<byte>> RequestWriter => _requestWriter;

    public ChannelReader<ReadOnlyMemory<byte>> RequestReader => _requests.Reader;

    public ChannelWriter<ReadOnlyMemory<byte>> ResponseWriter => _responseWriter;

    public ChannelReader<ReadOnlyMemory<byte>> ResponseReader => _responses.Reader;

    public void SetResponseMeta(ResponseMeta meta) => ResponseMeta = meta ?? new ResponseMeta();

    public ValueTask CompleteRequestsAsync(Error? error = null, CancellationToken cancellationToken = default)
    {
        if (_requestsCompleted)
        {
            return ValueTask.CompletedTask;
        }

        _requestsCompleted = true;
        TryCompleteChannel(_requests.Writer, error, RequestMeta.Transport);
        var status = ResolveCompletionStatus(error);
        _context.TrySetRequestCompletion(status, error);
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteResponsesAsync(Error? error = null, CancellationToken cancellationToken = default)
    {
        if (_responsesCompleted)
        {
            return ValueTask.CompletedTask;
        }

        _responsesCompleted = true;
        TryCompleteChannel(_responses.Writer, error, RequestMeta.Transport);
        var status = ResolveCompletionStatus(error);
        _context.TrySetResponseCompletion(status, error);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _requests.Writer.TryComplete();
        _responses.Writer.TryComplete();
        _context.TrySetRequestCompletion(StreamCompletionStatus.Cancelled);
        _context.TrySetResponseCompletion(StreamCompletionStatus.Cancelled);
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
