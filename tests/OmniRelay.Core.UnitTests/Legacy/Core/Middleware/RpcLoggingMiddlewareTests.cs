using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using OmniRelay.Tests.Support;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Core.Middleware;

public sealed class RpcLoggingMiddlewareTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask UnaryInbound_Success_LogsCompletion()
    {
        var logger = new TestLogger<RpcLoggingMiddleware>();
        var middleware = new RpcLoggingMiddleware(logger);

        var requestMeta = new RequestMeta(service: "svc", procedure: "echo::call", encoding: "application/json", transport: "grpc");
        var request = new Request<ReadOnlyMemory<byte>>(requestMeta, ReadOnlyMemory<byte>.Empty);
        var response = Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta(encoding: "application/json"));

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryInboundHandler)((req, token) => ValueTask.FromResult(Ok(response))));

        result.IsSuccess.ShouldBeTrue();
        TestLogger<RpcLoggingMiddleware>.LogEntry entry = logger.Entries.ShouldHaveSingleItem();
        entry.LogLevel.ShouldBe(LogLevel.Information);
        entry.Message.ShouldContain("inbound unary", Case.Insensitive);
        entry.Message.ShouldContain("svc", Case.Insensitive);
        entry.Message.ShouldContain("echo::call", Case.Insensitive);
        entry.Scope.ShouldNotBeNull();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask UnaryOutbound_Failure_LogsError()
    {
        var logger = new TestLogger<RpcLoggingMiddleware>();
        var middleware = new RpcLoggingMiddleware(logger);

        var requestMeta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");
        var request = new Request<ReadOnlyMemory<byte>>(requestMeta, ReadOnlyMemory<byte>.Empty);
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, "boom", transport: "grpc");

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryOutboundHandler)((req, token) => ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error))));

        result.IsFailure.ShouldBeTrue();
        TestLogger<RpcLoggingMiddleware>.LogEntry entry = logger.Entries.ShouldHaveSingleItem();
        entry.LogLevel.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("failed", Case.Insensitive);
        entry.Message.ShouldContain("boom", Case.Insensitive);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ShouldLogRequestFalse_SkipsSuccessLog()
    {
        var logger = new TestLogger<RpcLoggingMiddleware>();
        var options = new RpcLoggingOptions
        {
            ShouldLogRequest = _ => false
        };
        var middleware = new RpcLoggingMiddleware(logger, options);

        var requestMeta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");
        var request = new Request<ReadOnlyMemory<byte>>(requestMeta, ReadOnlyMemory<byte>.Empty);

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryInboundHandler)((req, token) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)))));

        result.IsSuccess.ShouldBeTrue();
        logger.Entries.ShouldBeEmpty();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Enrichment_AddsScopeWithRequestAndPeerDetails()
    {
        var logger = new TestLogger<RpcLoggingMiddleware>();
        var options = new RpcLoggingOptions
        {
            Enrich = (meta, response, activity) =>
            [
                new KeyValuePair<string, object?>("custom.key", "custom-value")
            ]
        };

        var middleware = new RpcLoggingMiddleware(logger, options);

        var headers = new[]
        {
            new KeyValuePair<string, string>("X-Request-Id", "req-123"),
            new KeyValuePair<string, string>("rpc.peer", "peer-1")
        };

        var requestMeta = new RequestMeta(
            service: "svc",
            procedure: "echo::call",
            encoding: "application/json",
            transport: "grpc",
            headers: headers);

        var request = new Request<ReadOnlyMemory<byte>>(requestMeta, ReadOnlyMemory<byte>.Empty);
        var responseMeta = new ResponseMeta(encoding: "application/json");

        using Activity activity = new Activity("test").Start();

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryInboundHandler)((req, token) =>
            {
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, responseMeta)));
            }));

        activity.Stop();

        result.IsSuccess.ShouldBeTrue();
        TestLogger<RpcLoggingMiddleware>.LogEntry entry = logger.Entries.ShouldHaveSingleItem();

        entry.Scope.ShouldNotBeNull();
        var scope = entry.Scope!;
        scope.ShouldContain(kvp =>
            kvp.Key == "rpc.request_id" &&
            string.Equals(kvp.Value == null ? null : kvp.Value.ToString(), "req-123", StringComparison.Ordinal));
        scope.ShouldContain(kvp =>
            kvp.Key == "rpc.peer" &&
            string.Equals(kvp.Value == null ? null : kvp.Value.ToString(), "peer-1", StringComparison.Ordinal));
        scope.ShouldContain(kvp =>
            kvp.Key == "activity.trace_id" &&
            !string.IsNullOrEmpty(kvp.Value == null ? null : kvp.Value.ToString()));
        scope.ShouldContain(kvp =>
            kvp.Key == "custom.key" &&
            string.Equals(kvp.Value == null ? null : kvp.Value.ToString(), "custom-value", StringComparison.Ordinal));
        scope.ShouldContain(kvp =>
            kvp.Key == "rpc.response_encoding" &&
            string.Equals(kvp.Value == null ? null : kvp.Value.ToString(), "application/json", StringComparison.Ordinal));
    }
}
