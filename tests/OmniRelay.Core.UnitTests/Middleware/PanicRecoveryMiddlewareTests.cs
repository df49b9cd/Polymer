using System.Threading.Channels;
using Hugo;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Core.UnitTests.Middleware;

public class PanicRecoveryMiddlewareTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ConvertsExceptionToError_UnaryOutbound()
    {
        var logger = Substitute.For<ILogger<PanicRecoveryMiddleware>>();
        var mw = new PanicRecoveryMiddleware(logger);
        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        UnaryOutboundHandler next = (req, ct) => throw new InvalidOperationException("boom");

        var result = await mw.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next);
        result.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).ShouldBe(OmniRelayStatusCode.Internal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ConvertsExceptionToError_AllShapes()
    {
        var middleware = new PanicRecoveryMiddleware();
        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "test");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var options = new StreamCallOptions(StreamDirection.Server);
        var ctx = new ClientStreamRequestContext(meta, Channel.CreateUnbounded<ReadOnlyMemory<byte>>().Reader);

        UnaryInboundHandler unaryInbound = (req, ct) => throw new ApplicationException("fail-unary-in");
        UnaryOutboundHandler unaryOutbound = (req, ct) => throw new ApplicationException("fail-unary-out");
        OnewayInboundHandler onewayInbound = (req, ct) => throw new ApplicationException("fail-oneway-in");
        OnewayOutboundHandler onewayOutbound = (req, ct) => throw new ApplicationException("fail-oneway-out");
        StreamInboundHandler streamInbound = (req, opt, ct) => throw new ApplicationException("fail-stream-in");
        StreamOutboundHandler streamOutbound = (req, opt, ct) => throw new ApplicationException("fail-stream-out");
        ClientStreamInboundHandler clientStreamInbound = (context, ct) => throw new ApplicationException("fail-client-stream-in");
        ClientStreamOutboundHandler clientStreamOutbound = (requestMeta, ct) => throw new ApplicationException("fail-client-stream-out");
        DuplexInboundHandler duplexInbound = (req, ct) => throw new ApplicationException("fail-duplex-in");
        DuplexOutboundHandler duplexOutbound = (req, ct) => throw new ApplicationException("fail-duplex-out");

        AssertFailure(await middleware.InvokeAsync(request, TestContext.Current.CancellationToken, unaryInbound));
        AssertFailure(await middleware.InvokeAsync(request, TestContext.Current.CancellationToken, unaryOutbound));
        AssertFailure(await middleware.InvokeAsync(request, TestContext.Current.CancellationToken, onewayInbound));
        AssertFailure(await middleware.InvokeAsync(request, TestContext.Current.CancellationToken, onewayOutbound));
        AssertFailure(await middleware.InvokeAsync(request, options, TestContext.Current.CancellationToken, streamInbound));
        AssertFailure(await middleware.InvokeAsync(request, options, TestContext.Current.CancellationToken, streamOutbound));
        AssertFailure(await middleware.InvokeAsync(ctx, TestContext.Current.CancellationToken, clientStreamInbound));
        AssertFailure(await middleware.InvokeAsync(meta, TestContext.Current.CancellationToken, clientStreamOutbound));
        AssertFailure(await middleware.InvokeAsync(request, TestContext.Current.CancellationToken, duplexInbound));
        AssertFailure(await middleware.InvokeAsync(request, TestContext.Current.CancellationToken, duplexOutbound));
    }

    private static void AssertFailure<T>(Result<T> result)
    {
        result.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).ShouldBe(OmniRelayStatusCode.Internal);
        result.Error!.Metadata.Keys.ShouldContain("exception_type");
    }
}
