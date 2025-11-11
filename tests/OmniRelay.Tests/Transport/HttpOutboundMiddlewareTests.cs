using System.Net;
using System.Text;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Transport.Http;
using OmniRelay.Transport.Http.Middleware;
using Xunit;

namespace OmniRelay.Tests.Transport;

public class HttpOutboundMiddlewareTests
{
    [Fact]
    public async Task UnaryPipeline_ExecutesInGlobalServiceProcedureOrder()
    {
        var callOrder = new List<string>();
        var capturedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var handler = new TestHttpMessageHandler(async request =>
        {
            if (request.Headers.TryGetValues("X-Global", out var globalValues))
            {
                capturedHeaders["X-Global"] = string.Join(',', globalValues);
            }

            if (request.Headers.TryGetValues("X-Service", out var serviceValues))
            {
                capturedHeaders["X-Service"] = string.Join(',', serviceValues);
            }

            if (request.Headers.TryGetValues("X-Procedure", out var procedureValues))
            {
                capturedHeaders["X-Procedure"] = string.Join(',', procedureValues);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("{\"message\":\"ok\"}"u8.ToArray())
            };
            response.Headers.TryAddWithoutValidation(HttpTransportHeaders.Encoding, "application/json");
            return await Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8080/")
        };

        var outbound = new HttpOutbound(httpClient, new Uri("http://localhost:8080/rpc"), disposeClient: true);

        var builder = new HttpOutboundMiddlewareBuilder();
        builder.Use(new RecordingMiddleware("global", callOrder, ctx => ctx.Request.Headers.Add("X-Global", "1")));
        builder.ForService("backend").Use(new RecordingMiddleware("service", callOrder, ctx => ctx.Request.Headers.Add("X-Service", "1")));
        builder.ForService("backend").ForProcedure("echo").Use(new RecordingMiddleware("procedure", callOrder, ctx => ctx.Request.Headers.Add("X-Procedure", "1")));

        var registry = builder.Build();
        Assert.NotNull(registry);

        ((IHttpOutboundMiddlewareSink)outbound).Attach("backend", registry!);
        var ct = TestContext.Current.CancellationToken;
        await outbound.StartAsync(ct);

        var requestMeta = new RequestMeta(
            service: "backend",
            procedure: "echo",
            encoding: "application/json",
            transport: "http");

        var payload = "{\"message\":\"ping\"}"u8.ToArray();
        var request = new Request<ReadOnlyMemory<byte>>(requestMeta, payload);

        var unary = (IUnaryOutbound)outbound;
        var result = await unary.CallAsync(request, ct);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new[] { "global", "service", "procedure" }, callOrder);
        Assert.Equal("1", capturedHeaders["X-Global"]);
        Assert.Equal("1", capturedHeaders["X-Service"]);
        Assert.Equal("1", capturedHeaders["X-Procedure"]);

        await outbound.StopAsync(ct);
    }

    [Fact]
    public async Task OnewayPipeline_InvokesMiddlewareAndReturnsAck()
    {
        var callOrder = new List<string>();

        var handler = new TestHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Accepted);
            response.Headers.TryAddWithoutValidation("X-Ack", "yes");
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5050/")
        };

        var outbound = new HttpOutbound(httpClient, new Uri("http://localhost:5050/rpc"), disposeClient: true);

        var builder = new HttpOutboundMiddlewareBuilder();
        builder.Use(new RecordingMiddleware("global", callOrder));
        builder.ForService("jobs").ForProcedure("enqueue").Use(new RecordingMiddleware("procedure", callOrder));

        var registry = builder.Build();
        Assert.NotNull(registry);

        ((IHttpOutboundMiddlewareSink)outbound).Attach("jobs", registry!);
        var ct = TestContext.Current.CancellationToken;
        await outbound.StartAsync(ct);

        var requestMeta = new RequestMeta(
            service: "jobs",
            procedure: "enqueue",
            transport: "http");

        var payload = new ReadOnlyMemory<byte>("task"u8.ToArray());
        var request = new Request<ReadOnlyMemory<byte>>(requestMeta, payload);

        var result = await ((IOnewayOutbound)outbound).CallAsync(request, ct);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new[] { "global", "procedure" }, callOrder);

        await outbound.StopAsync(ct);
    }

    private sealed class RecordingMiddleware(
        string id,
        IList<string> log,
        Action<HttpClientMiddlewareContext>? onInvoke = null)
        : IHttpClientMiddleware
    {
        public async ValueTask<HttpResponseMessage> InvokeAsync(
            HttpClientMiddlewareContext context,
            HttpClientMiddlewareHandler nextHandler,
            CancellationToken cancellationToken)
        {
            log.Add(id);
            onInvoke?.Invoke(context);
            return await nextHandler(context, cancellationToken);
        }
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync)
        : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _sendAsync(request);
    }
}
