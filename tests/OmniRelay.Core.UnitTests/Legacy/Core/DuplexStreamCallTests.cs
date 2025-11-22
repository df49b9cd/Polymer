using System.Threading.Channels;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Tests.Core;

public class DuplexStreamCallTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Create_WiresBidirectionalChannels()
    {
        var meta = new RequestMeta(service: "svc", procedure: "echo");
        var call = DuplexStreamCall.Create(meta);

        await call.RequestWriter.WriteAsync(new byte[] { 0x01 }, TestContext.Current.CancellationToken);
        await call.ResponseWriter.WriteAsync(new byte[] { 0x02 }, TestContext.Current.CancellationToken);

        call.RequestReader.TryRead(out var requestPayload).ShouldBeTrue();
        requestPayload.ToArray().ShouldBe(new byte[] { 0x01 });

        call.ResponseReader.TryRead(out var responsePayload).ShouldBeTrue();
        responsePayload.ToArray().ShouldBe(new byte[] { 0x02 });

        call.Context.RequestMessageCount.ShouldBe(1);
        call.Context.ResponseMessageCount.ShouldBe(1);

        await call.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Context_TracksCountsAndCompletions()
    {
        var meta = new RequestMeta(service: "svc", procedure: "echo", transport: "test");
        var call = DuplexStreamCall.Create(meta);

        await call.RequestWriter.WriteAsync(new byte[] { 0x01 }, TestContext.Current.CancellationToken);
        await call.RequestWriter.WriteAsync(new byte[] { 0x02 }, TestContext.Current.CancellationToken);
        await call.ResponseWriter.WriteAsync(new byte[] { 0x11 }, TestContext.Current.CancellationToken);

        call.Context.RequestMessageCount.ShouldBe(2);
        call.Context.ResponseMessageCount.ShouldBe(1);

        await call.CompleteRequestsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await call.CompleteResponsesAsync(cancellationToken: TestContext.Current.CancellationToken);

        call.Context.RequestCompletionStatus.ShouldBe(StreamCompletionStatus.Succeeded);
        call.Context.ResponseCompletionStatus.ShouldBe(StreamCompletionStatus.Succeeded);
        call.Context.RequestCompletionError.ShouldBeNull();
        call.Context.ResponseCompletionError.ShouldBeNull();
        call.Context.RequestCompletedAtUtc.HasValue.ShouldBeTrue();
        call.Context.ResponseCompletedAtUtc.HasValue.ShouldBeTrue();

        await call.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask CompleteResponsesAsync_WithErrorPropagatesException()
    {
        var meta = new RequestMeta(service: "svc", procedure: "echo", transport: "test");
        var call = DuplexStreamCall.Create(meta);
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, "boom", transport: "test");

        await call.CompleteResponsesAsync(error, TestContext.Current.CancellationToken);

        var readTask = call.ResponseReader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        var channelException = await Should.ThrowAsync<ChannelClosedException>(() => readTask);
        var omnirelayException = channelException.InnerException.ShouldBeOfType<OmniRelayException>();
        omnirelayException.StatusCode.ShouldBe(OmniRelayStatusCode.Internal);

        call.Context.ResponseCompletionStatus.ShouldBe(StreamCompletionStatus.Faulted);
        call.Context.ResponseCompletionError.ShouldBeSameAs(error);

        await call.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask CompleteRequestsAsync_WithCancelledErrorSetsCancelledStatus()
    {
        var meta = new RequestMeta(service: "svc", procedure: "echo", transport: "test");
        var call = DuplexStreamCall.Create(meta);
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Cancelled, "cancel", transport: "test");

        await call.CompleteRequestsAsync(error, TestContext.Current.CancellationToken);

        call.Context.RequestCompletionStatus.ShouldBe(StreamCompletionStatus.Cancelled);
        call.Context.RequestCompletionError.ShouldBeSameAs(error);

        await call.DisposeAsync();
    }
}
