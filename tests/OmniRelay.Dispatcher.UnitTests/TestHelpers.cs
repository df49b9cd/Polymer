using System.Collections.Concurrent;
using System.Threading.Channels;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using static Hugo.Go;

namespace OmniRelay.Dispatcher.UnitTests;

internal static class TestHelpers
{
    internal static Request<ReadOnlyMemory<byte>> CreateRequest(string transport = "test") =>
        new(new RequestMeta(service: "svc", transport: transport), ReadOnlyMemory<byte>.Empty);

    internal sealed class RecordingLifecycle : ILifecycle, IDispatcherAware
    {
        private readonly ConcurrentQueue<string> _events = new();

        public IReadOnlyCollection<string> Events => [.. _events];

        public static Dispatcher? BoundDispatcher { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            _events.Enqueue("start");
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            _events.Enqueue("stop");
            return ValueTask.CompletedTask;
        }

        public void Bind(Dispatcher dispatcher)
        {
            BoundDispatcher = dispatcher;
            _events.Enqueue("bind");
        }
    }

    internal sealed class DummyStreamCall : IStreamCall
    {
        private readonly Channel<ReadOnlyMemory<byte>> _requests = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Channel<ReadOnlyMemory<byte>> _responses = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public StreamDirection Direction => StreamDirection.Server;
        public RequestMeta RequestMeta => new();
        public ResponseMeta ResponseMeta => new();
        public StreamCallContext Context { get; } = new(StreamDirection.Server);

        public ChannelWriter<ReadOnlyMemory<byte>> Requests => _requests.Writer;
        public ChannelReader<ReadOnlyMemory<byte>> Responses => _responses.Reader;
        public ValueTask CompleteAsync(Error? error = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class DummyDuplexStreamCall : IDuplexStreamCall
    {
        private readonly Channel<ReadOnlyMemory<byte>> _requests = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Channel<ReadOnlyMemory<byte>> _responses = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public RequestMeta RequestMeta => new();
        public ResponseMeta ResponseMeta => new();
        public DuplexStreamCallContext Context { get; } = new();

        public ChannelWriter<ReadOnlyMemory<byte>> RequestWriter => _requests.Writer;
        public ChannelReader<ReadOnlyMemory<byte>> RequestReader => _requests.Reader;
        public ChannelWriter<ReadOnlyMemory<byte>> ResponseWriter => _responses.Writer;
        public ChannelReader<ReadOnlyMemory<byte>> ResponseReader => _responses.Reader;
        public ValueTask CompleteRequestsAsync(Error? error = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask CompleteResponsesAsync(Error? error = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DummyClientStreamTransportCall(RequestMeta requestMeta) : IClientStreamTransportCall
    {
        public RequestMeta RequestMeta { get; } = requestMeta;

        public ResponseMeta ResponseMeta => new();
        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> Response =>
            new(Task.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));
        public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class TestCodec<TRequest, TResponse> : ICodec<TRequest, TResponse>
    {
        public string Encoding { get; set; } = "test/encoding";

        public Result<byte[]> EncodeRequest(TRequest value, RequestMeta meta) => Ok(Array.Empty<byte>());
        public Result<TRequest> DecodeRequest(ReadOnlyMemory<byte> payload, RequestMeta meta) => Ok(default(TRequest)!);
        public Result<byte[]> EncodeResponse(TResponse value, ResponseMeta meta) => Ok(Array.Empty<byte>());
        public Result<TResponse> DecodeResponse(ReadOnlyMemory<byte> payload, ResponseMeta meta) => Ok(default(TResponse)!);
    }
}
