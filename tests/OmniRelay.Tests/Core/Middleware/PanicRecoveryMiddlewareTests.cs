using Microsoft.Extensions.Logging;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using OmniRelay.Tests.Support;
using Xunit;

namespace OmniRelay.Tests.Core.Middleware;

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
            (UnaryInboundHandler)((_, _) => throw new InvalidOperationException("boom")));

        Assert.True(result.IsFailure);
        var status = OmniRelayErrorAdapter.ToStatus(result.Error!);
        Assert.Equal(OmniRelayStatusCode.Internal, status);
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
            (UnaryOutboundHandler)((_, _) => throw new ApplicationException("fail")));

        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.Internal, OmniRelayErrorAdapter.ToStatus(result.Error!));
        Assert.Equal("System.ApplicationException", result.Error!.Metadata["exception_type"]);
    }
}
