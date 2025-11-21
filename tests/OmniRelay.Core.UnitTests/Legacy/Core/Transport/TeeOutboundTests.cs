using Hugo;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Core.Transport;

public class TeeOutboundTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Unary_ShadowInvokedWhenSampleRateSatisfied()
    {
        var primary = StubUnaryOutbound.Success();
        var shadow = StubUnaryOutbound.Success();
        var options = new TeeOptions { SampleRate = 1.0, LoggerFactory = NullLoggerFactory.Instance };

        var tee = new TeeUnaryOutbound(primary, shadow, options);
        var ct = TestContext.Current.CancellationToken;
        await tee.StartAsync(ct);

        try
        {
            var meta = new RequestMeta(service: "users", procedure: "users::get", transport: "test");
            var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

            var result = await tee.CallAsync(request, ct);

            result.IsSuccess.ShouldBeTrue();

            primary.CallCount.ShouldBe(1);
            SpinWait.SpinUntil(() => shadow.CallCount == 1, TimeSpan.FromSeconds(1)).ShouldBeTrue();

            shadow.LastRequestMeta.TryGetHeader("rpc-shadow", out var shadowHeader).ShouldBeTrue();
            shadowHeader.ShouldBe("true");
        }
        finally
        {
            await tee.StopAsync(ct);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Unary_DoesNotShadowWhenPrimaryFailsAndShadowOnSuccessOnly()
    {
        var primary = StubUnaryOutbound.Failure();
        var shadow = StubUnaryOutbound.Success();
        var tee = new TeeUnaryOutbound(primary, shadow, new TeeOptions { LoggerFactory = NullLoggerFactory.Instance });

        var meta = new RequestMeta(service: "svc", procedure: "svc::op");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        var ct = TestContext.Current.CancellationToken;
        var result = await tee.CallAsync(request, ct);

        result.IsSuccess.ShouldBeFalse();
        primary.CallCount.ShouldBe(1);
        shadow.CallCount.ShouldBe(0);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Unary_RespectsSampleRate()
    {
        var primary = StubUnaryOutbound.Success();
        var shadow = StubUnaryOutbound.Success();
        var tee = new TeeUnaryOutbound(primary, shadow, new TeeOptions { SampleRate = 0.0, LoggerFactory = NullLoggerFactory.Instance });

        var meta = new RequestMeta(service: "svc", procedure: "svc::op");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        var ct = TestContext.Current.CancellationToken;
        var result = await tee.CallAsync(request, ct);

        result.IsSuccess.ShouldBeTrue();
        primary.CallCount.ShouldBe(1);
        shadow.CallCount.ShouldBe(0);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Oneway_ShadowInvoked()
    {
        var primary = StubOnewayOutbound.Success();
        var shadow = StubOnewayOutbound.Success();
        var tee = new TeeOnewayOutbound(primary, shadow, new TeeOptions { LoggerFactory = NullLoggerFactory.Instance });

        var meta = new RequestMeta(service: "svc", procedure: "svc::notify");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        var ct = TestContext.Current.CancellationToken;
        var result = await tee.CallAsync(request, ct);

        result.IsSuccess.ShouldBeTrue();
        primary.CallCount.ShouldBe(1);
        SpinWait.SpinUntil(() => shadow.CallCount == 1, TimeSpan.FromSeconds(1)).ShouldBeTrue();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DiagnosticsExposePrimaryAndShadow()
    {
        var primary = StubUnaryOutbound.Success();
        var shadow = StubUnaryOutbound.Success();
        var tee = new TeeUnaryOutbound(primary, shadow, new TeeOptions { LoggerFactory = NullLoggerFactory.Instance });

        var diagnostics = tee.GetOutboundDiagnostics().ShouldBeOfType<TeeOutboundDiagnostics>();
        diagnostics.Primary.ShouldNotBeNull();
        diagnostics.Shadow.ShouldNotBeNull();
        diagnostics.SampleRate.ShouldBe(1.0);
    }

    private sealed class StubUnaryOutbound : IUnaryOutbound, IOutboundDiagnostic
    {
        private readonly Func<IRequest<ReadOnlyMemory<byte>>, Result<Response<ReadOnlyMemory<byte>>>> _behavior;

        private StubUnaryOutbound(Func<IRequest<ReadOnlyMemory<byte>>, Result<Response<ReadOnlyMemory<byte>>>> behavior)
        {
            _behavior = behavior;
        }

        public int CallCount { get; private set; }
        public RequestMeta LastRequestMeta { get; private set; } = new();
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public static StubUnaryOutbound Success() =>
            new(_ => Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())));

        public static StubUnaryOutbound Failure() =>
            new(_ => OmniRelayErrors.ToResult<Response<ReadOnlyMemory<byte>>>(OmniRelayStatusCode.Internal, "primary failed"));

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequestMeta = request.Meta;
            return ValueTask.FromResult(_behavior(request));
        }

        public object GetOutboundDiagnostics() => new { Kind = "stub-unary" };
    }

    private sealed class StubOnewayOutbound : IOnewayOutbound, IOutboundDiagnostic
    {
        private readonly Func<IRequest<ReadOnlyMemory<byte>>, Result<OnewayAck>> _behavior;

        private StubOnewayOutbound(Func<IRequest<ReadOnlyMemory<byte>>, Result<OnewayAck>> behavior)
        {
            _behavior = behavior;
        }

        public int CallCount { get; private set; }

        public static StubOnewayOutbound Success() =>
            new(_ => Ok(OnewayAck.Ack()));

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Result<OnewayAck>> CallAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(_behavior(request));
        }

        public object GetOutboundDiagnostics() => new { Kind = "stub-oneway" };
    }
}
