using System.Collections.Generic;
using System.Threading.Channels;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.ControlPlane.Agent;
using Xunit;

namespace OmniRelay.Core.UnitTests.ControlPlane.Agent;

public sealed class TelemetryForwarderTests
{
    [Fact]
    public async Task Forwarder_Batches_BySize()
    {
        var batches = new List<IReadOnlyList<string>>();
        var options = new TelemetryForwarder.TelemetryForwarderOptions(
            BatchSize: 3,
            FlushInterval: TimeSpan.FromSeconds(10),
            ChannelCapacity: 16,
            OnBatch: (batch, _) =>
            {
                batches.Add(batch);
                return ValueTask.CompletedTask;
            });

        await using var forwarder = new TelemetryForwarder(NullLogger<TelemetryForwarder>.Instance, options);

        forwarder.RecordSnapshot("v1");
        forwarder.RecordSnapshot("v2");
        forwarder.RecordSnapshot("v3"); // triggers batch
        forwarder.RecordSnapshot("v4");

        await Task.Delay(100, TestContext.Current.CancellationToken); // allow pump to process

        batches.ShouldHaveSingleItem();
        batches.First().Should().BeEquivalentTo(["v1", "v2", "v3"]);
    }

    [Fact]
    public async Task Forwarder_Flushes_OnInterval()
    {
        var batches = new List<IReadOnlyList<string>>();
        var options = new TelemetryForwarder.TelemetryForwarderOptions(
            BatchSize: 10,
            FlushInterval: TimeSpan.FromMilliseconds(100),
            ChannelCapacity: 16,
            OnBatch: (batch, _) =>
            {
                batches.Add(batch);
                return ValueTask.CompletedTask;
            });

        await using var forwarder = new TelemetryForwarder(NullLogger<TelemetryForwarder>.Instance, options);

        forwarder.RecordSnapshot("v1");
        forwarder.RecordSnapshot("v2");

        await Task.Delay(250, TestContext.Current.CancellationToken);


        batches.ShouldHaveSingleItem();
        batches.First().Should().BeEquivalentTo(["v1", "v2"]);
    }
}
