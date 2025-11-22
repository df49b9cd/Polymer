using System.Diagnostics;
using System.Reflection;
using Grpc.Core;
using OmniRelay.Transport.Grpc;
using Xunit;

namespace OmniRelay.Tests.Transport.Grpc;

public sealed class GrpcTransportDiagnosticsTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void StartClientActivity_NoListener_ReturnsNull()
    {
        var sourceField = typeof(GrpcTransportDiagnostics).GetField(
            "ActivitySource",
            BindingFlags.NonPublic | BindingFlags.Static);
        sourceField.ShouldNotBeNull();
        var source = (ActivitySource?)sourceField!.GetValue(null);
        source.ShouldNotBeNull();

        var activity = GrpcTransportDiagnostics.StartClientActivity(
            remoteService: "svc",
            procedure: "svc::Unary",
            address: new Uri("https://example.test:5001"),
            operation: "unary");

        if (source!.HasListeners())
        {
            activity.ShouldNotBeNull();
        }
        else
        {
            activity.ShouldBeNull();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void StartClientActivity_WithListener_PopulatesRpcAndNetworkTags()
    {
        var started = new List<Activity>();
        using var listener = CreateListener(started);

        using var activity = GrpcTransportDiagnostics.StartClientActivity(
            remoteService: "backend",
            procedure: "backend::Echo",
            address: new Uri("https://example.test:8443/echo"),
            operation: "unary");

        activity.ShouldNotBeNull();
        ((string?)activity!.GetTagItem("rpc.system")).ShouldBe("grpc");
        ((string?)activity.GetTagItem("rpc.service")).ShouldBe("backend");
        ((string?)activity.GetTagItem("rpc.method")).ShouldBe("backend::Echo");
        ((string?)activity.GetTagItem("net.peer.name")).ShouldBe("example.test");
        ((int)activity.GetTagItem("net.peer.port")!).ShouldBe(8443);

        using var ipActivity = GrpcTransportDiagnostics.StartClientActivity(
            remoteService: "backend",
            procedure: "backend::Echo",
            address: new Uri("https://127.0.0.1:9443/echo"),
            operation: "unary");

        ipActivity.ShouldNotBeNull();
        ((string?)ipActivity!.GetTagItem("net.peer.ip")).ShouldBe("127.0.0.1");
        ((int)ipActivity.GetTagItem("net.peer.port")!).ShouldBe(9443);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void SetStatusAndRecordException_UpdateActivityState()
    {
        var started = new List<Activity>();
        using var listener = CreateListener(started);

        using var activity = GrpcTransportDiagnostics.StartClientActivity(
            remoteService: "svc",
            procedure: "svc::Unary",
            address: new Uri("https://example.test:5900"),
            operation: "unary");

        activity.ShouldNotBeNull();

        GrpcTransportDiagnostics.SetStatus(activity, StatusCode.OK);
        activity!.Status.ShouldBe(ActivityStatusCode.Ok);

        var ex = new InvalidOperationException("boom");
        GrpcTransportDiagnostics.RecordException(activity, ex, StatusCode.Internal);

        activity.Status.ShouldBe(ActivityStatusCode.Error);
        var exceptionEvent = activity.Events.Where(evt => evt.Name == "exception").ShouldHaveSingleItem();
        exceptionEvent.Tags!.ShouldContain(tag => tag.Key == "exception.message" && Equals(tag.Value, "boom"));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ParseHttpProtocol_HandlesHttpAndCustomValues()
    {
        var parseMethod = typeof(GrpcTransportDiagnostics).GetMethod(
            "ParseHttpProtocol",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing ParseHttpProtocol.");

        var http3 = InvokeParse(parseMethod, "HTTP/3.0");
        http3.ShouldBe(("http", "3.0"));

        var http2 = InvokeParse(parseMethod, "HTTP/2");
        http2.ShouldBe(("http", "2"));

        var custom = InvokeParse(parseMethod, "grpc");
        custom.ShouldBe(("grpc", (string?)null));

        var missing = InvokeParse(parseMethod, null);
        missing.ShouldBe(((string?)null, (string?)null));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ExtractParentContext_ReturnsNullForInvalidTraceParent()
    {
        var extract = typeof(GrpcTransportDiagnostics).GetMethod(
            "ExtractParentContext",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing ExtractParentContext.");

        var metadata = new Metadata { { "traceparent", "not-a-valid-trace" } };
        var context = (ActivityContext?)extract!.Invoke(null, [metadata]);
        context.HasValue.ShouldBeFalse();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ExtractParentContext_ReturnsContextForValidTraceParent()
    {
        var extract = typeof(GrpcTransportDiagnostics).GetMethod(
            "ExtractParentContext",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing ExtractParentContext.");

        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        var metadata = new Metadata
        {
            { "traceparent", $"00-{traceId}-{spanId}-01" },
            { "tracestate", "congo=t61rcWkgMzE" }
        };

        var context = (ActivityContext?)extract!.Invoke(null, [metadata]);
        context.HasValue.ShouldBeTrue();
        var contextValue = context!.Value;
        contextValue.TraceId.ShouldBe(traceId);
        contextValue.SpanId.ShouldBe(spanId);
        contextValue.TraceState.ShouldBe("congo=t61rcWkgMzE");
    }

    private static (string? Name, string? Version) InvokeParse(MethodInfo parseMethod, string? protocol)
    {
        var result = parseMethod.Invoke(null, [protocol]);
        result.ShouldNotBeNull();
        return ((string? Name, string? Version))result!;
    }

    private static ActivityListener CreateListener(ICollection<Activity> started)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(
                source.Name,
                GrpcTransportDiagnostics.ActivitySourceName,
                StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => started.Add(activity!)
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

}
