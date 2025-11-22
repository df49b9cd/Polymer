using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Hugo;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Middleware;

public class RpcMetricsMiddlewareTests
{
    private sealed class Collector
    {
        public long Requests;
        public long Success;
        public long Failure;
        public List<double> Durations = [];
        public MeterListener Listener = new();

        public Collector(Meter meter, string prefix)
        {
            Listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter == meter)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            Listener.SetMeasurementEventCallback<long>((inst, value, tags, state) =>
            {
                if (inst.Name.EndsWith("requests"))
                {
                    Requests += value;
                }
                else if (inst.Name.EndsWith("success"))
                {
                    Success += value;
                }
                else if (inst.Name.EndsWith("failure"))
                {
                    Failure += value;
                }
            });
            Listener.SetMeasurementEventCallback<double>((inst, value, tags, state) =>
            {
                if (inst.Name.EndsWith("duration"))
                {
                    Durations.Add(value);
                }
            });
            Listener.Start();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Records_Success_And_Duration_For_Unary()
    {
        using var meter = new Meter("test.rpc.metrics.unary.success");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        UnaryOutboundHandler next = (req, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        var result = await middleware.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next);

        result.IsSuccess.ShouldBeTrue();
        await Task.Delay(10, TestContext.Current.CancellationToken);

        collector.Requests.ShouldBe(1);
        collector.Success.ShouldBe(1);
        collector.Failure.ShouldBe(0);
        collector.Durations.ShouldNotBeEmpty();
        collector.Listener.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Records_Failure_For_Unary()
    {
        using var meter = new Meter("test.rpc.metrics.unary.failure");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        UnaryOutboundHandler next = (req, ct) => ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "http")));
        var result = await middleware.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next);

        result.IsFailure.ShouldBeTrue();
        await Task.Delay(10, TestContext.Current.CancellationToken);

        collector.Requests.ShouldBe(1);
        collector.Success.ShouldBe(0);
        collector.Failure.ShouldBe(1);
        collector.Listener.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Records_Exception_For_Unary()
    {
        using var meter = new Meter("test.rpc.metrics.unary.exception");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        UnaryOutboundHandler next = (req, ct) => throw new InvalidOperationException("boom");

        await Should.ThrowAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next).AsTask());

        await Task.Delay(10, TestContext.Current.CancellationToken);

        collector.Requests.ShouldBe(1);
        collector.Success.ShouldBe(0);
        collector.Failure.ShouldBe(1);
        collector.Listener.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask StreamOutbound_DisposeRecordsOutcome()
    {
        using var meter = new Meter("test.rpc.metrics.stream");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var callOptions = new StreamCallOptions(StreamDirection.Server);

        StreamOutboundHandler next = (req, opts, ct) => ValueTask.FromResult(Ok<IStreamCall>(ServerStreamCall.Create(req.Meta)));

        var success = await middleware.InvokeAsync(request, callOptions, TestContext.Current.CancellationToken, next);
        success.IsSuccess.ShouldBeTrue();

        await success.Value.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        await success.Value.DisposeAsync();

        var failure = await middleware.InvokeAsync(request, callOptions, TestContext.Current.CancellationToken, next);
        failure.IsSuccess.ShouldBeTrue();

        await failure.Value.CompleteAsync(Error.Timeout(), TestContext.Current.CancellationToken);
        await failure.Value.DisposeAsync();

        await Task.Delay(10, TestContext.Current.CancellationToken);
        collector.Requests.ShouldBe(2);
        collector.Success.ShouldBe(1);
        collector.Failure.ShouldBe(1);
        collector.Listener.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ClientStreamOutbound_ResponseFailureRecorded()
    {
        using var meter = new Meter("test.rpc.metrics.clientstream");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");

        ClientStreamOutboundHandler next = (requestMeta, ct) =>
        {
            var call = new TestClientStreamCall(requestMeta, Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "http")));
            return ValueTask.FromResult(Ok<IClientStreamTransportCall>(call));
        };

        var result = await middleware.InvokeAsync(meta, TestContext.Current.CancellationToken, next);
        result.IsSuccess.ShouldBeTrue();

        _ = await result.Value.Response;
        await result.Value.DisposeAsync();

        await Task.Delay(10, TestContext.Current.CancellationToken);

        collector.Requests.ShouldBe(1);
        collector.Success.ShouldBe(0);
        collector.Failure.ShouldBe(1);
        collector.Listener.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ClientStreamOutbound_ResponseThrowsRecorded()
    {
        using var meter = new Meter("test.rpc.metrics.clientstream.throw");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");

        ClientStreamOutboundHandler next = (requestMeta, ct) =>
            ValueTask.FromResult(Ok<IClientStreamTransportCall>(new ThrowingClientStreamCall(requestMeta)));

        var result = await middleware.InvokeAsync(meta, TestContext.Current.CancellationToken, next);
        result.IsSuccess.ShouldBeTrue();

        await Should.ThrowAsync<InvalidOperationException>(async () => await result.Value.Response);

        await Task.Delay(10, TestContext.Current.CancellationToken);

        collector.Requests.ShouldBe(1);
        collector.Success.ShouldBe(0);
        collector.Failure.ShouldBe(1);
        collector.Listener.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask DuplexOutbound_CompletingWithErrorRecordsFailure()
    {
        using var meter = new Meter("test.rpc.metrics.duplex");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        DuplexOutboundHandler next = (req, ct) => ValueTask.FromResult(Ok<IDuplexStreamCall>(DuplexStreamCall.Create(req.Meta)));

        var result = await middleware.InvokeAsync(request, TestContext.Current.CancellationToken, next);
        result.IsSuccess.ShouldBeTrue();

        await result.Value.CompleteRequestsAsync(Error.Timeout(), TestContext.Current.CancellationToken);
        await result.Value.CompleteResponsesAsync(Error.Timeout(), TestContext.Current.CancellationToken);
        await result.Value.DisposeAsync();

        await Task.Delay(10, TestContext.Current.CancellationToken);

        collector.Requests.ShouldBe(1);
        collector.Success.ShouldBe(0);
        collector.Failure.ShouldBe(1);
        collector.Listener.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask OnewayOutbound_RecordsMetrics()
    {
        using var meter = new Meter("test.rpc.metrics.oneway");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        OnewayOutboundHandler next = (req, ct) => ValueTask.FromResult(Ok(OnewayAck.Ack()));
        var success = await middleware.InvokeAsync(request, TestContext.Current.CancellationToken, next);
        success.IsSuccess.ShouldBeTrue();

        OnewayOutboundHandler failingNext = (req, ct) => ValueTask.FromResult(Err<OnewayAck>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "http")));
        var failure = await middleware.InvokeAsync(request, TestContext.Current.CancellationToken, failingNext);
        failure.IsFailure.ShouldBeTrue();

        await Task.Delay(10, TestContext.Current.CancellationToken);

        collector.Requests.ShouldBe(2);
        collector.Success.ShouldBe(1);
        collector.Failure.ShouldBe(1);
        collector.Listener.Dispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ClientStreamInbound_RecordsSuccessAndFailure()
    {
        using var meter = new Meter("test.rpc.metrics.clientstream.inbound");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var context = new ClientStreamRequestContext(meta, Channel.CreateUnbounded<ReadOnlyMemory<byte>>().Reader);

        ClientStreamInboundHandler successDelegate = (ctx, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        var success = await middleware.InvokeAsync(context, TestContext.Current.CancellationToken, successDelegate);
        success.IsSuccess.ShouldBeTrue();

        ClientStreamInboundHandler failureDelegate = (ctx, ct) => ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "http")));
        var failure = await middleware.InvokeAsync(context, TestContext.Current.CancellationToken, failureDelegate);
        failure.IsFailure.ShouldBeTrue();

        await Task.Delay(10, TestContext.Current.CancellationToken);

        collector.Requests.ShouldBe(2);
        collector.Success.ShouldBe(1);
        collector.Failure.ShouldBe(1);
        collector.Listener.Dispose();
    }

    private sealed class ThrowingClientStreamCall(RequestMeta meta) : IClientStreamTransportCall
    {
        public RequestMeta RequestMeta { get; } = meta;
        public ResponseMeta ResponseMeta { get; } = new();
        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> Response =>
            new(Task.FromException<Result<Response<ReadOnlyMemory<byte>>>>(new InvalidOperationException("client stream failure")));
        public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestClientStreamCall(RequestMeta meta, Result<Response<ReadOnlyMemory<byte>>> response) : IClientStreamTransportCall
    {
        public RequestMeta RequestMeta { get; } = meta;
        public ResponseMeta ResponseMeta { get; } = new();
        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> Response { get; } =
            new(Task.FromResult(response));

        public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
