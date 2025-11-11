using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Hugo;
using NSubstitute;
using OmniRelay.Core;
using OmniRelay.Core.Diagnostics;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Middleware;

public class RpcTracingMiddlewareTests
{
    private sealed class TestRuntime : IDiagnosticsRuntime
    {
        public Microsoft.Extensions.Logging.LogLevel? MinimumLogLevel { get; private set; }
        public double? TraceSamplingProbability { get; private set; }
        public void SetMinimumLogLevel(Microsoft.Extensions.Logging.LogLevel? level) => MinimumLogLevel = level;
        public void SetTraceSamplingProbability(double? probability) => TraceSamplingProbability = probability;
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task OutboundUnary_InjectsTraceparent()
    {
        using var source = new ActivitySource("test.tracing");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.tracing",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        var mw = new RpcTracingMiddleware(null, new RpcTracingOptions { ActivitySource = source, InjectOutgoingContext = true });
        var meta = new RequestMeta(service: "svc", procedure: "proc");

        UnaryOutboundHandler next = (req, ct) =>
        {
            Assert.True(req.Meta.TryGetHeader("traceparent", out var tp) && !string.IsNullOrEmpty(tp));
            return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        };

        var res = await mw.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next);
        Assert.True(res.IsSuccess);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task InboundUnary_ExtractsParent_WhenPresent()
    {
        using var source = new ActivitySource("test.tracing");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.tracing",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        var mw = new RpcTracingMiddleware(null, new RpcTracingOptions { ActivitySource = source, ExtractIncomingContext = true });

        using var parent = source.StartActivity("parent", ActivityKind.Server);
        var meta = new RequestMeta(service: "svc", procedure: "proc").WithHeader("traceparent", parent!.Id!);

        Activity? captured = null;
        UnaryInboundHandler next = (req, ct) =>
        {
            captured = Activity.Current;
            return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        };

        var res = await mw.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next);
        Assert.True(res.IsSuccess);
        Assert.NotNull(captured);
        Assert.Equal(parent.TraceId, captured!.TraceId);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task SamplingProbabilityZero_DisablesActivity()
    {
        using var source = new ActivitySource("test.tracing");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.tracing",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        var runtime = new TestRuntime();
        runtime.SetTraceSamplingProbability(0.0);
        var mw = new RpcTracingMiddleware(runtime, new RpcTracingOptions { ActivitySource = source });

        Activity? captured = null;
        UnaryOutboundHandler next = (req, ct) =>
        {
            captured = Activity.Current;
            return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        };

        var res = await mw.InvokeAsync(new Request<ReadOnlyMemory<byte>>(new RequestMeta(service: "svc"), ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next);
        Assert.True(res.IsSuccess);
        Assert.Null(captured);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task OutboundUnary_ExceptionAddsEvent()
    {
        using var source = new ActivitySource("test.tracing.exception");
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.tracing.exception",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var mw = new RpcTracingMiddleware(null, new RpcTracingOptions { ActivitySource = source });
        var meta = new RequestMeta(service: "svc", procedure: "proc");
        UnaryOutboundHandler next = (req, ct) => throw new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mw.InvokeAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken, next).AsTask());

        Assert.Single(stoppedActivities);
        Assert.Contains(stoppedActivities[0].Events, evt => evt.Name == "exception");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task StreamOutbound_WrapsAndStopsActivity()
    {
        using var source = new ActivitySource("test.tracing.stream");
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.tracing.stream",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var middleware = new RpcTracingMiddleware(null, new RpcTracingOptions { ActivitySource = source });
        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var options = new StreamCallOptions(StreamDirection.Server);

        StreamOutboundHandler next = (req, opt, ct) => ValueTask.FromResult(Ok<IStreamCall>(ServerStreamCall.Create(req.Meta)));

        var result = await middleware.InvokeAsync(request, options, TestContext.Current.CancellationToken, next);
        Assert.True(result.IsSuccess);

        await result.Value.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        await result.Value.DisposeAsync();

        Assert.Single(stoppedActivities);
        Assert.Equal(ActivityStatusCode.Ok, stoppedActivities[0].Status);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ClientStreamOutbound_FailureSetsActivityError()
    {
        using var source = new ActivitySource("test.tracing.clientstream");
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.tracing.clientstream",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var middleware = new RpcTracingMiddleware(null, new RpcTracingOptions { ActivitySource = source });
        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");

        ClientStreamOutboundHandler next = (requestMeta, ct) =>
        {
            var call = Substitute.For<IClientStreamTransportCall>();
            call.RequestMeta.Returns(requestMeta);
            call.ResponseMeta.Returns(new ResponseMeta());
            var response = Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "http"));
            call.Response.Returns(new ValueTask<Result<Response<ReadOnlyMemory<byte>>>>(Task.FromResult(response)));
            call.WriteAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
            call.CompleteAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
            call.DisposeAsync().Returns(ValueTask.CompletedTask);
            return ValueTask.FromResult(Ok<IClientStreamTransportCall>(call));
        };

        var result = await middleware.InvokeAsync(meta, TestContext.Current.CancellationToken, next);
        Assert.True(result.IsSuccess);

        _ = await result.Value.Response;
        await result.Value.DisposeAsync();

        Assert.Single(stoppedActivities);
        Assert.Equal(ActivityStatusCode.Error, stoppedActivities[0].Status);
        Assert.Equal("fail", stoppedActivities[0].GetTagItem("rpc.error_message"));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task OnewayOutbound_FailureRecordsError()
    {
        using var source = new ActivitySource("test.tracing.oneway");
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.tracing.oneway",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var middleware = new RpcTracingMiddleware(null, new RpcTracingOptions { ActivitySource = source });
        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        OnewayOutboundHandler next = (req, ct) => ValueTask.FromResult(Err<OnewayAck>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "http")));

        var result = await middleware.InvokeAsync(request, TestContext.Current.CancellationToken, next);
        Assert.True(result.IsFailure);

        Assert.Single(stoppedActivities);
        Assert.Equal(ActivityStatusCode.Error, stoppedActivities[0].Status);
        Assert.Equal("fail", stoppedActivities[0].GetTagItem("rpc.error_message"));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task DuplexOutbound_ErrorOnCompletionStopsActivity()
    {
        using var source = new ActivitySource("test.tracing.duplex");
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.tracing.duplex",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var middleware = new RpcTracingMiddleware(null, new RpcTracingOptions { ActivitySource = source });
        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        DuplexOutboundHandler next = (req, ct) => ValueTask.FromResult(Ok<IDuplexStreamCall>(DuplexStreamCall.Create(req.Meta)));

        var result = await middleware.InvokeAsync(request, TestContext.Current.CancellationToken, next);
        Assert.True(result.IsSuccess);

        await result.Value.CompleteRequestsAsync(Error.Timeout(), TestContext.Current.CancellationToken);
        await result.Value.CompleteResponsesAsync(Error.Timeout(), TestContext.Current.CancellationToken);
        await result.Value.DisposeAsync();

        Assert.Single(stoppedActivities);
        Assert.Equal(ActivityStatusCode.Error, stoppedActivities[0].Status);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ClientStreamInbound_SetsSuccessStatus()
    {
        using var source = new ActivitySource("test.tracing.clientstream.in");
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.tracing.clientstream.in",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var middleware = new RpcTracingMiddleware(null, new RpcTracingOptions { ActivitySource = source });
        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var context = new ClientStreamRequestContext(meta, Channel.CreateUnbounded<ReadOnlyMemory<byte>>().Reader);

        ClientStreamInboundHandler next = (ctx, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        var result = await middleware.InvokeAsync(context, TestContext.Current.CancellationToken, next);

        Assert.True(result.IsSuccess);
        Assert.Single(stoppedActivities);
        Assert.Equal(ActivityStatusCode.Ok, stoppedActivities[0].Status);
    }
}
