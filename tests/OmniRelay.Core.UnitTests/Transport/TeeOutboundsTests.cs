using Hugo;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OmniRelay.Core.Transport;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Transport;

public class TeeOutboundsTests
{
    private static IRequest<ReadOnlyMemory<byte>> MakeRequest() => new Request<ReadOnlyMemory<byte>>(new RequestMeta(service: "svc", procedure: "proc"), new byte[] { 1, 2, 3 });

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask TeeUnary_Shadow_On_Success_With_Header()
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
        res.IsSuccess.ShouldBeTrue();

        // allow background task to run
        await Task.Delay(50, TestContext.Current.CancellationToken);
        captured.ShouldNotBeNull();
        captured!.Meta.TryGetHeader("rpc-shadow", out var val);
        val.ShouldBe("true");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask TeeUnary_Shadow_On_Failure_When_Allowed()
    {
        var primary = Substitute.For<IUnaryOutbound>();
        primary.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(Error.Canceled())));

        var shadowInvoked = new TaskCompletionSource<IRequest<ReadOnlyMemory<byte>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shadow = Substitute.For<IUnaryOutbound>();
        shadow.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                shadowInvoked.TrySetResult(ci.ArgAt<IRequest<ReadOnlyMemory<byte>>>(0));
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            });

        var options = new TeeOptions
        {
            SampleRate = 1.0,
            ShadowOnSuccessOnly = false,
            LoggerFactory = NullLoggerFactory.Instance
        };
        var tee = new TeeUnaryOutbound(primary, shadow, options);

        var req = MakeRequest();
        var res = await tee.CallAsync(req, TestContext.Current.CancellationToken);
        res.IsFailure.ShouldBeTrue();

        var captured = await shadowInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        captured.Meta.Service.ShouldBe(req.Meta.Service);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask TeeUnary_SampleRateZero_DisablesShadow()
    {
        var primary = Substitute.For<IUnaryOutbound>();
        primary.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));

        var shadow = Substitute.For<IUnaryOutbound>();
        var options = new TeeOptions
        {
            SampleRate = 0.0,
            LoggerFactory = NullLoggerFactory.Instance
        };
        var tee = new TeeUnaryOutbound(primary, shadow, options);

        var req = MakeRequest();
        await tee.CallAsync(req, TestContext.Current.CancellationToken);

        await Task.Delay(30, TestContext.Current.CancellationToken);
        await shadow.DidNotReceive().CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask TeeOneway_Shadow_Predicate_Blocks()
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
        res.IsSuccess.ShouldBeTrue();

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await shadow.DidNotReceive().CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Diagnostics_Aggregates_Children()
    {
        var primary = Substitute.For<IUnaryOutbound, IOutboundDiagnostic>();
        var shadow = Substitute.For<IUnaryOutbound, IOutboundDiagnostic>();
        ((IOutboundDiagnostic)primary).GetOutboundDiagnostics().Returns(new { name = "p" });
        ((IOutboundDiagnostic)shadow).GetOutboundDiagnostics().Returns(new { name = "s" });
        var tee = new TeeUnaryOutbound((IUnaryOutbound)primary, (IUnaryOutbound)shadow, new TeeOptions { LoggerFactory = NullLoggerFactory.Instance });
        var diag = tee.GetOutboundDiagnostics() as TeeOutboundDiagnostics;
        diag.ShouldNotBeNull();
        diag!.Primary.ShouldNotBeNull();
        diag.Shadow.ShouldNotBeNull();
        diag.ShadowHeaderName.ShouldBe("rpc-shadow");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TeeOptions_InvalidSampleRate_Throws()
    {
        var primary = Substitute.For<IUnaryOutbound>();
        var shadow = Substitute.For<IUnaryOutbound>();
        var options = new TeeOptions { SampleRate = 1.5, LoggerFactory = NullLoggerFactory.Instance };

        var ex = Should.Throw<ArgumentOutOfRangeException>(() => new TeeUnaryOutbound(primary, shadow, options));
        ex.Message.ShouldContain("Sample rate must be between 0.0 and 1.0 inclusive.");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask TeeUnary_StartAsync_ShadowFails_PrimaryStopped()
    {
        var primary = Substitute.For<IUnaryOutbound>();
        var shadow = Substitute.For<IUnaryOutbound>();

        shadow.StartAsync(Arg.Any<CancellationToken>())
            .Returns<ValueTask>(_ => throw new InvalidOperationException("fail"));

        var tee = new TeeUnaryOutbound(primary, shadow, new TeeOptions { LoggerFactory = NullLoggerFactory.Instance });

        await Should.ThrowAsync<InvalidOperationException>(() => tee.StartAsync(TestContext.Current.CancellationToken).AsTask());
        await primary.Received(1).StopAsync(Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask TeeUnary_BlankHeaderName_DoesNotModifyHeaders()
    {
        var primary = Substitute.For<IUnaryOutbound>();
        primary.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));

        var shadowInvoked = new TaskCompletionSource<IRequest<ReadOnlyMemory<byte>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shadow = Substitute.For<IUnaryOutbound>();
        shadow.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                shadowInvoked.TrySetResult(ci.ArgAt<IRequest<ReadOnlyMemory<byte>>>(0));
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            });

        var options = new TeeOptions
        {
            SampleRate = 1.0,
            ShadowHeaderName = "",
            LoggerFactory = NullLoggerFactory.Instance
        };
        var tee = new TeeUnaryOutbound(primary, shadow, options);

        var request = MakeRequest();
        await tee.CallAsync(request, TestContext.Current.CancellationToken);

        var captured = await shadowInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        captured.Meta.Headers.ContainsKey("rpc-shadow").ShouldBeFalse();
        captured.Meta.ShouldBeSameAs(request.Meta);
    }
}
