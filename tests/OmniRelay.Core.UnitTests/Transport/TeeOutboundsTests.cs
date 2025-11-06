using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Transport;

public class TeeOutboundsTests
{
    private static IRequest<ReadOnlyMemory<byte>> MakeRequest() => new Request<ReadOnlyMemory<byte>>(new RequestMeta(service: "svc", procedure: "proc"), new byte[] { 1, 2, 3 });

    [Fact]
    public async Task TeeUnary_Shadow_On_Success_With_Header()
    {
        var primary = Substitute.For<IUnaryOutbound>();
        primary.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));

        var shadow = Substitute.For<IUnaryOutbound>();
        IRequest<ReadOnlyMemory<byte>>? captured = null;
        shadow.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.ArgAt<IRequest<ReadOnlyMemory<byte>>>(0);
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            });

        var options = new TeeOptions
        {
            SampleRate = 1.0,
            ShadowOnSuccessOnly = true,
            LoggerFactory = NullLoggerFactory.Instance
        };
        var tee = new TeeUnaryOutbound(primary, shadow, options);

        var req = MakeRequest();
        var res = await tee.CallAsync(req, TestContext.Current.CancellationToken);
        Assert.True(res.IsSuccess);

        // allow background task to run
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.NotNull(captured);
        captured!.Meta.TryGetHeader("rpc-shadow", out var val);
        Assert.Equal("true", val);
    }

    [Fact]
    public async Task TeeOneway_Shadow_Predicate_Blocks()
    {
        var primary = Substitute.For<IOnewayOutbound>();
        primary.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(Ok(OnewayAck.Ack())));

        var shadow = Substitute.For<IOnewayOutbound>();
        var options = new TeeOptions
        {
            SampleRate = 1.0,
            Predicate = _ => false,
            LoggerFactory = NullLoggerFactory.Instance
        };
        var tee = new TeeOnewayOutbound(primary, shadow, options);

        var req = MakeRequest();
        var res = await tee.CallAsync(req, TestContext.Current.CancellationToken);
        Assert.True(res.IsSuccess);

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await shadow.DidNotReceive().CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Diagnostics_Aggregates_Children()
    {
        var primary = Substitute.For<IUnaryOutbound, IOutboundDiagnostic>();
        var shadow = Substitute.For<IUnaryOutbound, IOutboundDiagnostic>();
        ((IOutboundDiagnostic)primary).GetOutboundDiagnostics().Returns(new { name = "p" });
        ((IOutboundDiagnostic)shadow).GetOutboundDiagnostics().Returns(new { name = "s" });
        var tee = new TeeUnaryOutbound((IUnaryOutbound)primary, (IUnaryOutbound)shadow, new TeeOptions { LoggerFactory = NullLoggerFactory.Instance });
        var diag = tee.GetOutboundDiagnostics() as TeeOutboundDiagnostics;
        Assert.NotNull(diag);
        Assert.NotNull(diag!.Primary);
        Assert.NotNull(diag.Shadow);
        Assert.Equal("rpc-shadow", diag.ShadowHeaderName);
    }
}
