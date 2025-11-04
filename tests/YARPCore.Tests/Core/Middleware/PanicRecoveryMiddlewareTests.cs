using Microsoft.Extensions.Logging;
using Xunit;
using YARPCore.Core;
using YARPCore.Core.Middleware;
using YARPCore.Core.Transport;
using YARPCore.Errors;
using YARPCore.Tests.Support;

namespace YARPCore.Tests.Core.Middleware;

public sealed class PanicRecoveryMiddlewareTests
{
    [Fact]
    public async Task UnaryInbound_Exception_ConvertedToInternalError()
    {
        var logger = new TestLogger<PanicRecoveryMiddleware>();
        var middleware = new PanicRecoveryMiddleware(logger);

        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryInboundDelegate)((_, _) => throw new InvalidOperationException("boom")));

        Assert.True(result.IsFailure);
        var status = PolymerErrorAdapter.ToStatus(result.Error!);
        Assert.Equal(PolymerStatusCode.Internal, status);
        Assert.Equal("System.InvalidOperationException", result.Error!.Metadata["exception_type"]);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.Contains("Unhandled exception", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnaryOutbound_Exception_ConvertedToInternalError()
    {
        var middleware = new PanicRecoveryMiddleware();
        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryOutboundDelegate)((_, _) => throw new ApplicationException("fail")));

        Assert.True(result.IsFailure);
        Assert.Equal(PolymerStatusCode.Internal, PolymerErrorAdapter.ToStatus(result.Error!));
        Assert.Equal("System.ApplicationException", result.Error!.Metadata["exception_type"]);
    }
}
