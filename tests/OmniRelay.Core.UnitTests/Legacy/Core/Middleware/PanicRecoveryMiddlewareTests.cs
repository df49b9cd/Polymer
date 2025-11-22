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
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask UnaryInbound_Exception_ConvertedToInternalError()
    {
        var logger = new TestLogger<PanicRecoveryMiddleware>();
        var middleware = new PanicRecoveryMiddleware(logger);

        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryInboundHandler)((_, _) => throw new InvalidOperationException("boom")));

        result.IsFailure.ShouldBeTrue();
        var status = OmniRelayErrorAdapter.ToStatus(result.Error!);
        status.ShouldBe(OmniRelayStatusCode.Internal);
        result.Error!.Metadata["exception_type"].ShouldBe("System.InvalidOperationException");

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.LogLevel.ShouldBe(LogLevel.Error);
        entry.Message.ShouldContain("Unhandled exception", Case.Insensitive);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask UnaryOutbound_Exception_ConvertedToInternalError()
    {
        var middleware = new PanicRecoveryMiddleware();
        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryOutboundHandler)((_, _) => throw new ApplicationException("fail")));

        result.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).ShouldBe(OmniRelayStatusCode.Internal);
        result.Error!.Metadata["exception_type"].ShouldBe("System.ApplicationException");
    }
}
