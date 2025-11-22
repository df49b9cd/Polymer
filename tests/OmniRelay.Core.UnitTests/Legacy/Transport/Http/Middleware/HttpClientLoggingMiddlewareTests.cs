using System.Net;
using OmniRelay.Core;
using OmniRelay.Tests.Support;
using OmniRelay.Transport.Http.Middleware;
using Xunit;

namespace OmniRelay.Tests.Transport.Http.Middleware;

public sealed class HttpClientLoggingMiddlewareTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask InvokeAsync_AttachesRequestScope()
    {
        var logger = new TestLogger<HttpClientLoggingMiddleware>();
        var middleware = new HttpClientLoggingMiddleware(logger);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:8080/foo");
        var meta = new RequestMeta(
            service: "svc",
            procedure: "echo::call",
            transport: "http",
            headers:
            [
                new KeyValuePair<string, string>("x-request-id", "req-123"),
                new KeyValuePair<string, string>("rpc.peer", "10.0.0.1")
            ]);

        var context = new HttpClientMiddlewareContext(
            request,
            meta,
            HttpOutboundCallKind.Unary,
            HttpCompletionOption.ResponseContentRead);

        using var response = await middleware.InvokeAsync(
            context,
            static (_, _) => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        logger.Entries.Count.ShouldBe(2);

        foreach (var entry in logger.Entries)
        {
            entry.Scope.ShouldNotBeNull();
            entry.Scope!.ShouldContain(pair =>
                pair.Key == "rpc.request_id" &&
                string.Equals(pair.Value == null ? null : pair.Value.ToString(), "req-123", StringComparison.Ordinal));
            entry.Scope.ShouldContain(pair =>
                pair.Key == "rpc.peer" &&
                string.Equals(pair.Value == null ? null : pair.Value.ToString(), "10.0.0.1", StringComparison.Ordinal));
        }
    }
}
