using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Hugo;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Middleware;

public class PanicRecoveryMiddlewareTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConvertsExceptionToError_UnaryOutbound()
    {
        var logger = Substitute.For<ILogger<PanicRecoveryMiddleware>>();
        var mw = new PanicRecoveryMiddleware(logger);
        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        UnaryOutboundHandler next = (req, ct) => throw new InvalidOperationException("boom");

        var result = await mw.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next);
        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.Internal, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConvertsExceptionToError_AllShapes()
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
        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.Internal, OmniRelayErrorAdapter.ToStatus(result.Error!));
        Assert.Contains("exception_type", result.Error!.Metadata.Keys);
    }
}
