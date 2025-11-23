using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Threading.Channels;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.IntegrationTests;

public sealed class HttpTransportMetricsTests
{
    private sealed class StubStreamCall : IStreamCall
    {
        public StubStreamCall(ChannelReader<ReadOnlyMemory<byte>> responses)
        {
            Responses = responses;
        }

        public StreamDirection Direction => StreamDirection.Server;
        public RequestMeta RequestMeta { get; } = new("svc", "proc", transport: "http");
        public ResponseMeta ResponseMeta { get; } = new();
        public StreamCallContext Context { get; } = new(StreamDirection.Server);
        public ChannelWriter<ReadOnlyMemory<byte>> Requests { get; } = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }).Writer;
        public ChannelReader<ReadOnlyMemory<byte>> Responses { get; }
        public ValueTask CompleteAsync(Error? fault = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PumpServerStream_Emits_PerItem_Telemetry()
    {
        using var listener = new MeterListener();
        long responseMessages = 0;
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == HttpTransportMetrics.MeterName && instrument.Name == "omnirelay.http.server_stream.response_messages")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((inst, value, tags, state) =>
        {
            if (inst.Name == "omnirelay.http.server_stream.response_messages")
            {
                responseMessages += value;
            }
        });
        listener.Start();

        var responses = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        await responses.Writer.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
        await responses.Writer.WriteAsync(new byte[] { 2 }, TestContext.Current.CancellationToken);
        responses.Writer.TryComplete();

        var stub = new StubStreamCall(responses.Reader);
        var pipe = new Pipe();

        var method = typeof(HttpInbound).GetMethod("PumpServerStreamAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .CreateDelegate<Func<IStreamCall, PipeWriter, ResponseMeta, TimeSpan?, int?, string, KeyValuePair<string, object?>[], CancellationToken, ValueTask<Result<Hugo.Go.Unit>>>>();

        var result = await method(stub, pipe.Writer, new ResponseMeta(), null, null, "http", Array.Empty<KeyValuePair<string, object?>>(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();

        // Allow listener to process callbacks
        await Task.Delay(20, TestContext.Current.CancellationToken);
        responseMessages.ShouldBe(2);
    }
}
