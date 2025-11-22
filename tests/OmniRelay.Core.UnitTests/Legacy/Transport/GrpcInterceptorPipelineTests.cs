using Grpc.Core;
using Grpc.Core.Interceptors;
using OmniRelay.Transport.Grpc.Interceptors;
using Xunit;

namespace OmniRelay.Tests.Transport;

public class GrpcInterceptorPipelineTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ClientIntercepts_InvokeGlobalServiceProcedureOrder()
    {
        var log = new List<string>();

        var builder = new GrpcTransportInterceptorBuilder();
        builder.UseClient(new RecordingClientInterceptor("global", log));
        builder.ForClientService("backend").Use(new RecordingClientInterceptor("service", log));
        builder.ForClientService("backend").ForProcedure("Echo").Use(new RecordingClientInterceptor("procedure", log));

        var registry = builder.BuildClientRegistry();
        registry.ShouldNotBeNull();

        var composite = new CompositeClientInterceptor(registry!, "backend");
        var invoker = new RecordingCallInvoker(log);
        var interceptedInvoker = invoker.Intercept(composite);

        var marshaller = Marshallers.Create(static payload => payload, static data => data);
        var method = new Method<byte[], byte[]>(MethodType.Unary, "backend", "Echo", marshaller, marshaller);

        var call = interceptedInvoker.AsyncUnaryCall(method, host: null, options: new CallOptions(), request: []);
        var response = await call.ResponseAsync;

        response.ShouldBeEmpty();
        log.ShouldBe(new[] { "global", "service", "procedure", "terminal" });
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ServerIntercepts_InvokeGlobalProcedureOrder()
    {
        var log = new List<string>();

        var builder = new GrpcTransportInterceptorBuilder();
        builder.UseServer(new RecordingServerInterceptor("global", log));
        builder.ForServerProcedure("Echo").Use(new RecordingServerInterceptor("procedure", log));

        var registry = builder.BuildServerRegistry();
        registry.ShouldNotBeNull();

        var composite = new CompositeServerInterceptor(registry!);

        var context = new TestServerCallContext("/backend/Echo");
        var result = await composite.UnaryServerHandler(
            Array.Empty<byte>(),
            context,
            (request, callContext) =>
            {
                log.Add("terminal");
                return Task.FromResult(Array.Empty<byte>());
            });

        result.ShouldBeEmpty();
        log.ShouldBe(new[] { "global", "procedure", "terminal" });
    }

    private sealed class RecordingClientInterceptor(string id, IList<string> log) : Interceptor
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
            where TRequest : class
            where TResponse : class
        {
            log.Add(id);
            return continuation(request, context);
        }
    }

    private sealed class RecordingServerInterceptor(string id, IList<string> log) : Interceptor
    {
        public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request,
            ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
            where TRequest : class
            where TResponse : class
        {
            log.Add(id);
            return continuation(request, context);
        }
    }

    private sealed class RecordingCallInvoker(IList<string> log) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            log.Add("terminal");
            var responseTask = Task.FromResult((TResponse)(object)Array.Empty<byte>());
            return new AsyncUnaryCall<TResponse>(
                responseTask,
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                () => [],
                static () => { });
        }

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options) => throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options) => throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) => throw new NotSupportedException();

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) => throw new NotSupportedException();
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
}
