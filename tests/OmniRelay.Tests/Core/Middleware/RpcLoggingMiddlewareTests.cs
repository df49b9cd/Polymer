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
    [Fact]
    public async Task UnaryInbound_Success_LogsCompletion()
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

        Assert.True(result.IsSuccess);
        TestLogger<RpcLoggingMiddleware>.LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.LogLevel);
        Assert.Contains("inbound unary", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("svc", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("echo::call", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(entry.Scope);
    }

    [Fact]
    public async Task UnaryOutbound_Failure_LogsError()
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

        Assert.True(result.IsFailure);
        TestLogger<RpcLoggingMiddleware>.LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.LogLevel);
        Assert.Contains("failed", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("boom", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShouldLogRequestFalse_SkipsSuccessLog()
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

        Assert.True(result.IsSuccess);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Enrichment_AddsScopeWithRequestAndPeerDetails()
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

        Assert.True(result.IsSuccess);
        TestLogger<RpcLoggingMiddleware>.LogEntry entry = Assert.Single(logger.Entries);

        Assert.NotNull(entry.Scope);
        var scope = entry.Scope!;
        Assert.Contains(scope, kvp => kvp.Key == "rpc.request_id" && string.Equals(kvp.Value?.ToString(), "req-123", StringComparison.Ordinal));
        Assert.Contains(scope, kvp => kvp.Key == "rpc.peer" && string.Equals(kvp.Value?.ToString(), "peer-1", StringComparison.Ordinal));
        Assert.Contains(scope, kvp => kvp.Key == "activity.trace_id" && !string.IsNullOrEmpty(kvp.Value?.ToString()));
        Assert.Contains(scope, kvp => kvp.Key == "custom.key" && string.Equals(kvp.Value?.ToString(), "custom-value", StringComparison.Ordinal));
        Assert.Contains(scope, kvp => kvp.Key == "rpc.response_encoding" && string.Equals(kvp.Value?.ToString(), "application/json", StringComparison.Ordinal));
    }
}
