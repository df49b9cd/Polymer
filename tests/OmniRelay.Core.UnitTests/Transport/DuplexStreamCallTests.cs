using System;
using System.Threading;
using System.Threading.Channels;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Core.UnitTests.Transport;

public class DuplexStreamCallTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask CompleteRequestsAndResponses_SetStatuses()
    {
        var meta = new RequestMeta(service: "svc", transport: "http");
        var call = DuplexStreamCall.Create(meta);

        var reqErr = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Cancelled, "cancel");
        await call.CompleteRequestsAsync(reqErr, TestContext.Current.CancellationToken);
        call.Context.RequestCompletionStatus.ShouldBe(StreamCompletionStatus.Cancelled);
        call.Context.RequestCompletedAtUtc.ShouldNotBeNull();

        await call.CompleteResponsesAsync(null, TestContext.Current.CancellationToken);
        call.Context.ResponseCompletionStatus.ShouldBe(StreamCompletionStatus.Succeeded);
        call.Context.ResponseCompletedAtUtc.ShouldNotBeNull();

        await call.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask CompleteResponsesAsync_WithCancelledToken_PropagatesCancellation()
    {
        var meta = new RequestMeta(service: "svc", transport: "grpc");
        var call = DuplexStreamCall.Create(meta);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await call.CompleteResponsesAsync(cancellationToken: cts.Token);

        call.Context.ResponseCompletionStatus.ShouldBe(StreamCompletionStatus.Cancelled);

        var closed = await Should.ThrowAsync<ChannelClosedException>(async () =>
            await call.ResponseReader.ReadAsync(TestContext.Current.CancellationToken));
        var relayException = closed.InnerException.ShouldBeOfType<OmniRelayException>();
        relayException.Message.ShouldBe("The response stream was cancelled.");

        await call.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask CompleteRequestsAsync_WithCancelledToken_PropagatesCancellation()
    {
        var meta = new RequestMeta(service: "svc", transport: "grpc");
        var call = DuplexStreamCall.Create(meta);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await call.CompleteRequestsAsync(cancellationToken: cts.Token);

        call.Context.RequestCompletionStatus.ShouldBe(StreamCompletionStatus.Cancelled);

        var closed = await Should.ThrowAsync<ChannelClosedException>(async () =>
            await call.RequestReader.ReadAsync(TestContext.Current.CancellationToken));
        var relayException = closed.InnerException.ShouldBeOfType<OmniRelayException>();
        relayException.Message.ShouldBe("The request stream was cancelled.");

        await call.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask BoundedChannels_ApplyBackpressure()
    {
        var meta = new RequestMeta(service: "svc", transport: "grpc");
        var call = DuplexStreamCall.Create(meta, channelCapacity: 1);

        await call.RequestWriter.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await call.RequestWriter.WaitToWriteAsync(cts.Token));

        var dequeued = await call.RequestReader.ReadAsync(TestContext.Current.CancellationToken);
        dequeued.ToArray().ShouldBe([1]);

        var waitOk = await call.RequestWriter.WaitToWriteAsync(TestContext.Current.CancellationToken);
        waitOk.ShouldBeTrue();
        call.RequestWriter.TryWrite(new byte[] { 2 }).ShouldBeTrue();

        await call.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Dispose_CompletesChannels()
    {
        var meta = new RequestMeta(service: "svc", transport: "grpc");
        var call = DuplexStreamCall.Create(meta, channelCapacity: 2);

        await call.DisposeAsync();

        await Should.ThrowAsync<ChannelClosedException>(async () =>
            await call.RequestWriter.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken));

        var canRead = await call.ResponseReader.WaitToReadAsync(TestContext.Current.CancellationToken);
        canRead.ShouldBeFalse();
    }
}
