using System.Threading.Channels;
using Hugo;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Transport;

/// <summary>
/// Client-streaming call container that buffers request messages and completes with a unary response.
/// </summary>
public sealed class ClientStreamCall : IAsyncDisposable
{
    private readonly Channel<ReadOnlyMemory<byte>> _requests;
    private readonly TaskCompletionSource<Result<Response<ReadOnlyMemory<byte>>>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    private ClientStreamCall(RequestMeta meta, Channel<ReadOnlyMemory<byte>> requests)
    {
        RequestMeta = meta ?? throw new ArgumentNullException(nameof(meta));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        ResponseMeta = new ResponseMeta();
    }

    /// <summary>Gets the request metadata.</summary>
    public RequestMeta RequestMeta { get; }

    /// <summary>Gets the response metadata.</summary>
    public ResponseMeta ResponseMeta { get; private set; }

    /// <summary>Gets the request writer channel.</summary>
    public ChannelWriter<ReadOnlyMemory<byte>> Requests => _requests.Writer;

    internal ChannelReader<ReadOnlyMemory<byte>> Reader => _requests.Reader;

    /// <summary>Gets the ValueTask that completes with the unary response.</summary>
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> Response =>
        new(_completion.Task);

    /// <summary>
    /// Creates a client-streaming call instance for the given request metadata.
    /// </summary>
    public static ClientStreamCall Create(RequestMeta meta)
    {
        var requests = MakeChannel<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        return new ClientStreamCall(meta, requests);
    }

    internal void TryComplete(Result<Response<ReadOnlyMemory<byte>>> result)
    {
        if (result.IsSuccess)
        {
            ResponseMeta = result.Value.Meta;
        }

        _completion.TrySetResult(result);
    }

    internal void TryCompleteWithError(Error error)
    {
        var err = Err<Response<ReadOnlyMemory<byte>>>(error);
        _completion.TrySetResult(err);
    }

    /// <summary>
    /// Completes the request writer and optionally propagates an error to the reader.
    /// </summary>
    public ValueTask CompleteWriterAsync(Error? error = null, CancellationToken cancellationToken = default)
    {
        if (error is null)
        {
            _requests.Writer.TryComplete();
        }
        else
        {
            var exception = OmniRelayErrors.FromError(error, RequestMeta.Transport ?? "grpc");
            _requests.Writer.TryComplete(exception);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _requests.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
