using Grpc.Core;
using Xunit;
using YARPCore.Errors;
using YARPCore.Transport.Grpc;

namespace YARPCore.Tests.Transport;

public sealed class GrpcExceptionAdapterInterceptorTests
{
    [Fact]
    public async Task UnaryServerHandler_ConvertsExceptionToRpcStatus()
    {
        var interceptor = new GrpcExceptionAdapterInterceptor();
        var context = new TestServerCallContext("svc/method");

        var rpcException = await Assert.ThrowsAsync<RpcException>(() =>
            interceptor.UnaryServerHandler<object, object>(
                new object(),
                context,
                (request, callContext) => throw new TimeoutException("deadline")));

        Assert.Equal(StatusCode.DeadlineExceeded, rpcException.StatusCode);
        Assert.Equal(PolymerStatusCode.DeadlineExceeded.ToString(), rpcException.Trailers.GetValue(GrpcTransportConstants.StatusTrailer));
        Assert.Equal("grpc", rpcException.Trailers.GetValue("YARPCore.transport"));
    }

    [Fact]
    public async Task ServerStreamingHandler_PassesThroughRpcException()
    {
        var interceptor = new GrpcExceptionAdapterInterceptor();
        var context = new TestServerCallContext("svc/method");
        var expected = new RpcException(new Status(StatusCode.Internal, "boom"));

        var actual = await Assert.ThrowsAsync<RpcException>(() =>
            interceptor.ServerStreamingServerHandler(
                new object(),
                new NoopServerStreamWriter<object>(),
                context,
                (request, stream, callContext) => throw expected));

        Assert.Same(expected, actual);
    }

    private sealed class TestServerCallContext(
        string method,
        string host = "localhost",
        string peer = "ipv4:127.0.0.1:50051",
        DateTime? deadline = null,
        Metadata? requestHeaders = null,
        CancellationToken cancellationToken = default) : ServerCallContext
    {
        private readonly string _method = method;
        private readonly string _host = host;
        private readonly string _peer = peer;
        private readonly DateTime _deadline = deadline ?? DateTime.UtcNow.AddMinutes(1);
        private readonly Metadata _requestHeaders = requestHeaders ?? [];
        private readonly CancellationToken _cancellationToken = cancellationToken;
        private readonly Metadata _responseTrailers = [];
        private readonly AuthContext _authContext = new(null, []);

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override string MethodCore => _method;

        protected override string HostCore => _host;

        protected override string PeerCore => _peer;

        protected override DateTime DeadlineCore => _deadline;

        protected override Metadata RequestHeadersCore => _requestHeaders;

        protected override CancellationToken CancellationTokenCore => _cancellationToken;

        protected override Metadata ResponseTrailersCore => _responseTrailers;

        protected override Status StatusCore { get; set; }

        protected override WriteOptions? WriteOptionsCore { get; set; }

        protected override AuthContext AuthContextCore => _authContext;
    }

    private sealed class NoopServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message) => Task.CompletedTask;
    }
}
