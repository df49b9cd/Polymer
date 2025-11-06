using System.Threading.Channels;
using Hugo;
using OmniRelay.Errors;

namespace OmniRelay.Core.Transport;

/// <summary>
/// Server-streaming call used by transports to emit response messages.
/// </summary>
public sealed class ServerStreamCall : IStreamCall
{
    private readonly Channel<ReadOnlyMemory<byte>> _responses;
    private readonly Channel<ReadOnlyMemory<byte>> _requests;
    private readonly StreamCallContext _context;
    private bool _completed;

    private ServerStreamCall(RequestMeta requestMeta, ResponseMeta responseMeta)
    {
        RequestMeta = requestMeta ?? throw new ArgumentNullException(nameof(requestMeta));
        ResponseMeta = responseMeta ?? new ResponseMeta();
        _context = new StreamCallContext(StreamDirection.Server);

        _responses = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _requests = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        _requests.Writer.TryComplete();
    }

    /// <summary>
    /// Creates a server-streaming call instance.
    /// </summary>
    public static ServerStreamCall Create(RequestMeta requestMeta, ResponseMeta? responseMeta = null) =>
        new(requestMeta, responseMeta ?? new ResponseMeta());

    /// <inheritdoc />
    public StreamDirection Direction => StreamDirection.Server;

    /// <inheritdoc />
    public RequestMeta RequestMeta { get; }

    /// <inheritdoc />
    public ResponseMeta ResponseMeta { get; private set; }

    /// <inheritdoc />
    public StreamCallContext Context => _context;

    /// <inheritdoc />
    public ChannelWriter<ReadOnlyMemory<byte>> Requests => _requests.Writer;

    /// <inheritdoc />
    public ChannelReader<ReadOnlyMemory<byte>> Responses => _responses.Reader;

    /// <summary>Updates the response metadata.</summary>
    public void SetResponseMeta(ResponseMeta meta) => ResponseMeta = meta ?? new ResponseMeta();

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await _responses.Writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        _context.IncrementMessageCount();
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(Error? error = null, CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            return ValueTask.CompletedTask;
        }

        _completed = true;
        var status = ResolveCompletionStatus(error);

        if (error is null)
        {
            _responses.Writer.TryComplete();
        }
        else
        {
            var transport = RequestMeta.Transport ?? "stream";
            var exception = OmniRelayErrors.FromError(error, transport);
            _responses.Writer.TryComplete(exception);
        }

        _context.TrySetCompletion(status, error);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _responses.Writer.TryComplete();
        _requests.Writer.TryComplete();
        _context.TrySetCompletion(StreamCompletionStatus.Cancelled);
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
}
