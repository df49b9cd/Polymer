using Grpc.Core;
using OmniRelay.Errors;
using OmniRelay.Transport.Grpc;
using Xunit;

namespace OmniRelay.Tests.Transport;

public sealed class GrpcExceptionAdapterInterceptorTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask UnaryServerHandler_ConvertsExceptionToRpcStatus()
    {
        var interceptor = new GrpcExceptionAdapterInterceptor();
        var context = new TestServerCallContext("svc/method");

        var rpcException = await Should.ThrowAsync<RpcException>(() =>
            interceptor.UnaryServerHandler<object, object>(
                new object(),
                context,
                (request, callContext) => throw new TimeoutException("deadline")));

        rpcException.StatusCode.ShouldBe(StatusCode.DeadlineExceeded);
        rpcException.Trailers.GetValue(GrpcTransportConstants.StatusTrailer).ShouldBe(nameof(OmniRelayStatusCode.DeadlineExceeded));
        rpcException.Trailers.GetValue(GrpcTransportConstants.TransportTrailer).ShouldBe("grpc");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ServerStreamingHandler_PassesThroughRpcException()
    {
        var interceptor = new GrpcExceptionAdapterInterceptor();
        var context = new TestServerCallContext("svc/method");
        var expected = new RpcException(new Status(StatusCode.Internal, "boom"));

        var actual = await Should.ThrowAsync<RpcException>(() =>
            interceptor.ServerStreamingServerHandler(
                new object(),
                new NoopServerStreamWriter<object>(),
                context,
                (request, stream, callContext) => throw expected));

        actual.ShouldBeSameAs(expected);
    }

    private sealed class TestServerCallContext(
        string method,
        string host = "localhost",
        string peer = "ipv4:127.0.0.1:50051",
        DateTime? deadline = null,
        Metadata? requestHeaders = null,
        CancellationToken cancellationToken = default) : ServerCallContext
    {
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override string MethodCore { get; } = method;

        protected override string HostCore { get; } = host;

        protected override string PeerCore { get; } = peer;

        protected override DateTime DeadlineCore { get; } = deadline ?? DateTime.UtcNow.AddMinutes(1);

        protected override Metadata RequestHeadersCore { get; } = requestHeaders ?? [];

        protected override CancellationToken CancellationTokenCore { get; } = cancellationToken;

        protected override Metadata ResponseTrailersCore { get; } = [];

        protected override Status StatusCore { get; set; }

        protected override WriteOptions? WriteOptionsCore { get; set; }

        protected override AuthContext AuthContextCore { get; } = new(null, []);
    }

    private sealed class NoopServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message) => Task.CompletedTask;
    }
}
