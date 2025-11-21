using System.Diagnostics.Metrics;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Core.Middleware;

public sealed class RpcMetricsMiddlewareTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask UnaryInbound_Success_RecordsMetrics()
    {
        using var meter = new Meter("test.yarpcore.metrics");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var middleware = new RpcMetricsMiddleware(options);

        using var listener = CreateListener(meter, out var longMeasurements, out var doubleMeasurements);

        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc", encoding: "application/json");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var response = Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta(encoding: "application/json"));

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryInboundHandler)((req, token) => ValueTask.FromResult(Ok(response))));

        result.IsSuccess.ShouldBeTrue();

        AssertMeasurement(longMeasurements, "test.rpc.requests", 1, "rpc.direction", "inbound");
        AssertMeasurement(longMeasurements, "test.rpc.success", 1, "rpc.direction", "inbound");
        AssertDoesNotContain(longMeasurements, "test.rpc.failure");

        var duration = doubleMeasurements.Single(m => m.InstrumentName == "test.rpc.duration");
        duration.Tags.Single(tag => tag.Key == "rpc.direction").Value.ShouldBe("inbound");
        (duration.Value > 0).ShouldBeTrue();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask UnaryOutbound_Failure_IncrementsFailureCounter()
    {
        using var meter = new Meter("test.yarpcore.metrics");
        var options = new RpcMetricsOptions { Meter = meter, MetricPrefix = "test.rpc" };
        var middleware = new RpcMetricsMiddleware(options);

        using var listener = CreateListener(meter, out var longMeasurements, out var doubleMeasurements);

        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc", encoding: "application/json");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, "boom", transport: "grpc");

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryOutboundHandler)((req, token) => ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error))));

        result.IsFailure.ShouldBeTrue();

        AssertMeasurement(longMeasurements, "test.rpc.requests", 1, "rpc.direction", "outbound");
        AssertMeasurement(longMeasurements, "test.rpc.failure", 1, "rpc.direction", "outbound", "rpc.status", nameof(OmniRelayStatusCode.Internal));
        AssertDoesNotContain(longMeasurements, "test.rpc.success");

        var duration = doubleMeasurements.Single(m => m.InstrumentName == "test.rpc.duration");
        duration.Tags.Single(tag => tag.Key == "rpc.direction").Value.ShouldBe("outbound");
        duration.Tags.Single(tag => tag.Key == "rpc.status").Value.ShouldBe(nameof(OmniRelayStatusCode.Internal));
    }

    private static MeterListener CreateListener(
        Meter meter,
        out List<LongMeasurement> longMeasurements,
        out List<DoubleMeasurement> doubleMeasurements)
    {
        var longList = new List<LongMeasurement>();
        var doubleList = new List<DoubleMeasurement>();

        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter == meter)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            longList.Add(new LongMeasurement(instrument.Name, measurement, tags.ToArray()));
        });

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            doubleList.Add(new DoubleMeasurement(instrument.Name, measurement, tags.ToArray()));
        });

        listener.Start();
        longMeasurements = longList;
        doubleMeasurements = doubleList;
        return listener;
    }

    private static void AssertMeasurement(
        IEnumerable<LongMeasurement> measurements,
        string instrumentName,
        long expectedValue,
        params string[] expectedTagPairs)
    {
        var measurement = measurements.Single(m => m.InstrumentName == instrumentName);
        measurement.Value.ShouldBe(expectedValue);
        AssertExpectedTags(measurement.Tags, expectedTagPairs);
    }

    private static void AssertDoesNotContain(IEnumerable<LongMeasurement> measurements, string instrumentName) =>
        measurements.ShouldNotContain(measurement => measurement.InstrumentName == instrumentName);

    private static void AssertExpectedTags(IReadOnlyList<KeyValuePair<string, object?>> tags, string[] expectedTagPairs)
    {
        for (var index = 0; index < expectedTagPairs.Length; index += 2)
        {
            var key = expectedTagPairs[index];
            var value = expectedTagPairs[index + 1];
            tags.ShouldContain(tag => tag.Key == key && Equals(tag.Value, value));
        }
    }

    private readonly record struct LongMeasurement(string InstrumentName, long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags);
    private readonly record struct DoubleMeasurement(string InstrumentName, double Value, IReadOnlyList<KeyValuePair<string, object?>> Tags);
}
