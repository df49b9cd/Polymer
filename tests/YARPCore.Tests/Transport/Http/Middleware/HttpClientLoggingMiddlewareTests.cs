using System.Net;
using Xunit;
using YARPCore.Core;
using YARPCore.Tests.Support;
using YARPCore.Transport.Http.Middleware;

namespace YARPCore.Tests.Transport.Http.Middleware;

public sealed class HttpClientLoggingMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_AttachesRequestScope()
    {
        var logger = new TestLogger<HttpClientLoggingMiddleware>();
        var middleware = new HttpClientLoggingMiddleware(logger);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:8080/foo");
        var meta = new RequestMeta(
            service: "svc",
            procedure: "echo::call",
            transport: "http",
            headers: new[]
            {
                new KeyValuePair<string, string>("x-request-id", "req-123"),
                new KeyValuePair<string, string>("rpc.peer", "10.0.0.1")
            });

        var context = new HttpClientMiddlewareContext(
            request,
            meta,
            HttpOutboundCallKind.Unary,
            HttpCompletionOption.ResponseContentRead);

        var response = await middleware.InvokeAsync(
            context,
            CancellationToken.None,
            static (_, _) => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, logger.Entries.Count);

        foreach (var entry in logger.Entries)
        {
            Assert.NotNull(entry.Scope);
            Assert.Contains(entry.Scope!, pair => pair.Key == "rpc.request_id" && string.Equals(pair.Value?.ToString(), "req-123", StringComparison.Ordinal));
            Assert.Contains(entry.Scope!, pair => pair.Key == "rpc.peer" && string.Equals(pair.Value?.ToString(), "10.0.0.1", StringComparison.Ordinal));
        }
    }
}
