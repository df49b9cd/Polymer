using System.Diagnostics;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Core.Middleware;

public sealed class RpcTracingMiddlewareTests
{
    [Fact]
    public async Task UnaryInbound_CreatesServerSpanWithTags()
    {
        var activities = new List<Activity>();
        using var source = new ActivitySource("test.yarpcore.tracing");
        using var listener = CreateListener(source.Name, activities);
        var options = new RpcTracingOptions { ActivitySource = source };
        var middleware = new RpcTracingMiddleware(options: options);

        var parent = new Activity("parent");
        parent.SetIdFormat(ActivityIdFormat.W3C);
        parent.Start();
        var parentId = parent.Id!;
        parent.Stop();

        var meta = new RequestMeta(
            service: "svc",
            procedure: "echo::call",
            transport: "grpc",
            encoding: "application/json",
            headers:
            [
                new KeyValuePair<string, string>("traceparent", parentId)
            ]);

        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var response = Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta(encoding: "application/json"));

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryInboundHandler)((req, token) => ValueTask.FromResult(Ok(response))));

        Assert.True(result.IsSuccess);
        var activity = Assert.Single(activities);
        Assert.Equal(ActivityKind.Server, activity.Kind);
        Assert.Equal("yarpcore.rpc.inbound.unary", activity.DisplayName);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Equal(parent.TraceId, activity.TraceId);
        Assert.Equal("svc", activity.GetTagItem("rpc.service"));
        Assert.Equal("echo::call", activity.GetTagItem("rpc.method"));
        Assert.Equal("grpc", activity.GetTagItem("rpc.transport"));
        Assert.Equal("application/json", activity.GetTagItem("rpc.response.encoding"));
    }

    [Fact]
    public async Task UnaryOutbound_Failure_InjectsTraceContextAndSetsError()
    {
        var activities = new List<Activity>();
        using var source = new ActivitySource("test.yarpcore.tracing");
        using var listener = CreateListener(source.Name, activities);
        var options = new RpcTracingOptions { ActivitySource = source };
        var middleware = new RpcTracingMiddleware(options: options);

        RequestMeta? capturedMeta = null;
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, "boom", transport: "grpc");

        var meta = new RequestMeta(
            service: "svc",
            procedure: "echo::call",
            transport: "grpc",
            encoding: "application/json");

        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryOutboundHandler)((req, token) =>
            {
                capturedMeta = req.Meta;
                return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
            }));

        Assert.True(result.IsFailure);
        Assert.NotNull(capturedMeta);
        Assert.True(capturedMeta!.TryGetHeader("traceparent", out var traceParentHeader));
        Assert.False(string.IsNullOrEmpty(traceParentHeader));

        var activity = Assert.Single(activities);
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("boom", activity.GetTagItem("rpc.error_message"));
        Assert.Equal(traceParentHeader, activity.Id);
    }

    private static ActivityListener CreateListener(string sourceName, List<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == sourceName,
            Sample = (ref _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => activities.Add(activity),
            ActivityStopped = _ => { }
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
