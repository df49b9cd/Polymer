using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Hugo;
using OmniRelay.Core;
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
        public List<double> Durations = new();
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
                if (inst.Name.EndsWith("requests")) Requests += value;
                else if (inst.Name.EndsWith("success")) Success += value;
                else if (inst.Name.EndsWith("failure")) Failure += value;
            });
            Listener.SetMeasurementEventCallback<double>((inst, value, tags, state) =>
            {
                if (inst.Name.EndsWith("duration")) Durations.Add(value);
            });
            Listener.Start();
        }
    }

    [Fact]
    public async Task Records_Success_And_Duration_For_Unary()
    {
        using var meter = new Meter("test.rpc.metrics.unary.success");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        UnaryOutboundDelegate next = (req, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        var result = await middleware.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next);

        Assert.True(result.IsSuccess);
        await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(1, collector.Requests);
        Assert.Equal(1, collector.Success);
        Assert.Equal(0, collector.Failure);
        Assert.NotEmpty(collector.Durations);
        collector.Listener.Dispose();
    }

    [Fact]
    public async Task Records_Failure_For_Unary()
    {
        using var meter = new Meter("test.rpc.metrics.unary.failure");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        UnaryOutboundDelegate next = (req, ct) => ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "http")));
        var result = await middleware.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next);

        Assert.True(result.IsFailure);
        await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(1, collector.Requests);
        Assert.Equal(0, collector.Success);
        Assert.Equal(1, collector.Failure);
        collector.Listener.Dispose();
    }

    [Fact]
    public async Task StreamOutbound_DisposeRecordsOutcome()
    {
        using var meter = new Meter("test.rpc.metrics.stream");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var callOptions = new StreamCallOptions(StreamDirection.Server);

        StreamOutboundDelegate next = (req, opts, ct) => ValueTask.FromResult(Ok<IStreamCall>(ServerStreamCall.Create(req.Meta)));

        var success = await middleware.InvokeAsync(request, callOptions, TestContext.Current.CancellationToken, next);
        Assert.True(success.IsSuccess);

        await success.Value.CompleteAsync();
        await success.Value.DisposeAsync();

        var failure = await middleware.InvokeAsync(request, callOptions, TestContext.Current.CancellationToken, next);
        Assert.True(failure.IsSuccess);

        await failure.Value.CompleteAsync(Error.Timeout());
        await failure.Value.DisposeAsync();

        await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(2, collector.Requests);
        Assert.Equal(1, collector.Success);
        Assert.Equal(1, collector.Failure);
        collector.Listener.Dispose();
    }

    [Fact]
    public async Task ClientStreamOutbound_ResponseFailureRecorded()
    {
        using var meter = new Meter("test.rpc.metrics.clientstream");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");

        ClientStreamOutboundDelegate next = (requestMeta, ct) =>
        {
            var call = new TestClientStreamCall(requestMeta, Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "http")));
            return ValueTask.FromResult(Ok<IClientStreamTransportCall>(call));
        };

        var result = await middleware.InvokeAsync(meta, TestContext.Current.CancellationToken, next);
        Assert.True(result.IsSuccess);

        _ = await result.Value.Response;
        await result.Value.DisposeAsync();

        await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(1, collector.Requests);
        Assert.Equal(0, collector.Success);
        Assert.Equal(1, collector.Failure);
        collector.Listener.Dispose();
    }

    [Fact]
    public async Task DuplexOutbound_CompletingWithErrorRecordsFailure()
    {
        using var meter = new Meter("test.rpc.metrics.duplex");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        DuplexOutboundDelegate next = (req, ct) => ValueTask.FromResult(Ok<IDuplexStreamCall>(DuplexStreamCall.Create(req.Meta)));

        var result = await middleware.InvokeAsync(request, TestContext.Current.CancellationToken, next);
        Assert.True(result.IsSuccess);

        await result.Value.CompleteRequestsAsync(Error.Timeout(), TestContext.Current.CancellationToken);
        await result.Value.CompleteResponsesAsync(Error.Timeout());
        await result.Value.DisposeAsync();

        await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(1, collector.Requests);
        Assert.Equal(0, collector.Success);
        Assert.Equal(1, collector.Failure);
        collector.Listener.Dispose();
    }

    [Fact]
    public async Task OnewayOutbound_RecordsMetrics()
    {
        using var meter = new Meter("test.rpc.metrics.oneway");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        OnewayOutboundDelegate next = (req, ct) => ValueTask.FromResult(Ok(OnewayAck.Ack()));
        var success = await middleware.InvokeAsync(request, TestContext.Current.CancellationToken, next);
        Assert.True(success.IsSuccess);

        OnewayOutboundDelegate failingNext = (req, ct) => ValueTask.FromResult(Err<OnewayAck>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "http")));
        var failure = await middleware.InvokeAsync(request, TestContext.Current.CancellationToken, failingNext);
        Assert.True(failure.IsFailure);

        await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(2, collector.Requests);
        Assert.Equal(1, collector.Success);
        Assert.Equal(1, collector.Failure);
        collector.Listener.Dispose();
    }

    [Fact]
    public async Task ClientStreamInbound_RecordsSuccessAndFailure()
    {
        using var meter = new Meter("test.rpc.metrics.clientstream.inbound");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var collector = new Collector(meter, options.MetricPrefix);
        var middleware = new RpcMetricsMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var context = new ClientStreamRequestContext(meta, Channel.CreateUnbounded<ReadOnlyMemory<byte>>().Reader);

        ClientStreamInboundDelegate successDelegate = (ctx, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        var success = await middleware.InvokeAsync(context, TestContext.Current.CancellationToken, successDelegate);
        Assert.True(success.IsSuccess);

        ClientStreamInboundDelegate failureDelegate = (ctx, ct) => ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "http")));
        var failure = await middleware.InvokeAsync(context, TestContext.Current.CancellationToken, failureDelegate);
        Assert.True(failure.IsFailure);

        await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(2, collector.Requests);
        Assert.Equal(1, collector.Success);
        Assert.Equal(1, collector.Failure);
        collector.Listener.Dispose();
    }

    private sealed class TestClientStreamCall(RequestMeta meta, Result<Response<ReadOnlyMemory<byte>>> response) : IClientStreamTransportCall
    {
        public RequestMeta RequestMeta { get; } = meta;
        public ResponseMeta ResponseMeta { get; } = new();
        public Task<Result<Response<ReadOnlyMemory<byte>>>> Response { get; } = Task.FromResult(response);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
