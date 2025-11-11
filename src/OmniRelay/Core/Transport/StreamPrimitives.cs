using System.Threading.Channels;
using Hugo;

namespace OmniRelay.Core.Transport;

public enum StreamDirection
{
    Server,
    Client,
    Bidirectional
}

public sealed record StreamCallOptions(StreamDirection Direction)
{
    public StreamDirection Direction { get; init; } = Direction;
}

public interface IStreamCall : IAsyncDisposable
{
    StreamDirection Direction { get; }
    RequestMeta RequestMeta { get; }
    ResponseMeta ResponseMeta { get; }
    StreamCallContext Context { get; }
    ChannelWriter<ReadOnlyMemory<byte>> Requests { get; }
    ChannelReader<ReadOnlyMemory<byte>> Responses { get; }
    ValueTask CompleteAsync(Error? error = null, CancellationToken cancellationToken = default);
}

public interface IDuplexStreamCall : IAsyncDisposable
{
    RequestMeta RequestMeta { get; }
    ResponseMeta ResponseMeta { get; }
    DuplexStreamCallContext Context { get; }
    ChannelWriter<ReadOnlyMemory<byte>> RequestWriter { get; }
    ChannelReader<ReadOnlyMemory<byte>> RequestReader { get; }
    ChannelWriter<ReadOnlyMemory<byte>> ResponseWriter { get; }
    ChannelReader<ReadOnlyMemory<byte>> ResponseReader { get; }
    ValueTask CompleteRequestsAsync(Error? error = null, CancellationToken cancellationToken = default);
    ValueTask CompleteResponsesAsync(Error? error = null, CancellationToken cancellationToken = default);
}
