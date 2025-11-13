using System.Threading.Channels;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Core.UnitTests.Transport;

public class DuplexStreamCallTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CompleteRequestsAndResponses_SetStatuses()
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
    public async Task CompleteResponsesAsync_WithCancelledToken_PropagatesCancellation()
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
    public async Task CompleteRequestsAsync_WithCancelledToken_PropagatesCancellation()
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
}
