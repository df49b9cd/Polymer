using System.Threading.Channels;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;

namespace OmniRelay.Transport.Http;

public sealed class HttpStreamCall : IStreamCall
{
    private readonly Channel<ReadOnlyMemory<byte>> _responses;
    private readonly Channel<ReadOnlyMemory<byte>> _requests;
    private readonly StreamCallContext _context;
    private bool _completed;

    private HttpStreamCall(RequestMeta requestMeta, ResponseMeta responseMeta)
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
        _requests.Writer.TryComplete(); // Server streaming does not consume client payloads.
    }

    public static HttpStreamCall CreateServerStream(RequestMeta requestMeta, ResponseMeta? responseMeta = null) =>
        new(requestMeta, responseMeta ?? new ResponseMeta());

    public StreamDirection Direction => StreamDirection.Server;

    public RequestMeta RequestMeta { get; }

    public ResponseMeta ResponseMeta { get; private set; }

    public StreamCallContext Context => _context;

    public ChannelWriter<ReadOnlyMemory<byte>> Requests => _requests.Writer;

    public ChannelReader<ReadOnlyMemory<byte>> Responses => _responses.Reader;

    public void SetResponseMeta(ResponseMeta responseMeta) => ResponseMeta = responseMeta ?? new ResponseMeta();

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await _responses.Writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        _context.IncrementMessageCount();
    }

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
            var exception = OmniRelayErrors.FromError(error, "http");
            _responses.Writer.TryComplete(exception);
        }

        _context.TrySetCompletion(status, error);

        return ValueTask.CompletedTask;
    }

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
