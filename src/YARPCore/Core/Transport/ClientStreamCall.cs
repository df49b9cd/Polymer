using System.Threading.Channels;
using Hugo;
using YARPCore.Errors;
using static Hugo.Go;

namespace YARPCore.Core.Transport;

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

    public RequestMeta RequestMeta { get; }

    public ResponseMeta ResponseMeta { get; private set; }

    public ChannelWriter<ReadOnlyMemory<byte>> Requests => _requests.Writer;

    internal ChannelReader<ReadOnlyMemory<byte>> Reader => _requests.Reader;

    public Task<Result<Response<ReadOnlyMemory<byte>>>> Response => _completion.Task;

    public static ClientStreamCall Create(RequestMeta meta)
    {
        var requests = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
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

    public ValueTask CompleteWriterAsync(Error? error = null, CancellationToken cancellationToken = default)
    {
        if (error is null)
        {
            _requests.Writer.TryComplete();
        }
        else
        {
            var exception = PolymerErrors.FromError(error, RequestMeta.Transport ?? "grpc");
            _requests.Writer.TryComplete(exception);
        }

        return ValueTask.CompletedTask;
    }

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
