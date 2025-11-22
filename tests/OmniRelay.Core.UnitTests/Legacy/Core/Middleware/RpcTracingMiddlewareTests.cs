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
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask UnaryInbound_CreatesServerSpanWithTags()
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

        result.IsSuccess.ShouldBeTrue();
        var activity = activities.ShouldHaveSingleItem();
        activity.Kind.ShouldBe(ActivityKind.Server);
        activity.DisplayName.ShouldBe("yarpcore.rpc.inbound.unary");
        activity.Status.ShouldBe(ActivityStatusCode.Ok);
        activity.TraceId.ShouldBe(parent.TraceId);
        activity.GetTagItem("rpc.service").ShouldBe("svc");
        activity.GetTagItem("rpc.method").ShouldBe("echo::call");
        activity.GetTagItem("rpc.transport").ShouldBe("grpc");
        activity.GetTagItem("rpc.response.encoding").ShouldBe("application/json");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask UnaryOutbound_Failure_InjectsTraceContextAndSetsError()
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

        result.IsFailure.ShouldBeTrue();
        capturedMeta.ShouldNotBeNull();
        capturedMeta!.TryGetHeader("traceparent", out var traceParentHeader).ShouldBeTrue();
        string.IsNullOrEmpty(traceParentHeader).ShouldBeFalse();

        var activity = activities.ShouldHaveSingleItem();
        activity.Kind.ShouldBe(ActivityKind.Client);
        activity.Status.ShouldBe(ActivityStatusCode.Error);
        activity.GetTagItem("rpc.error_message").ShouldBe("boom");
        activity.Id.ShouldBe(traceParentHeader);
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
