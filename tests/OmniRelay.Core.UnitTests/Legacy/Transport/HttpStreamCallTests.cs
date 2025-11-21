using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Tests.Transport;

public class HttpStreamCallTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Context_TracksMessagesAndSuccessCompletion()
    {
        var meta = new RequestMeta(service: "svc", procedure: "stream", transport: "http");
        var call = HttpStreamCall.CreateServerStream(meta);

        await call.WriteAsync(new byte[] { 0x01 }, TestContext.Current.CancellationToken);
        await call.WriteAsync(new byte[] { 0x02 }, TestContext.Current.CancellationToken);

        call.Context.MessageCount.ShouldBe(2);

        await call.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);

        call.Context.CompletionStatus.ShouldBe(StreamCompletionStatus.Succeeded);
        call.Context.CompletionError.ShouldBeNull();
        call.Context.CompletedAtUtc.HasValue.ShouldBeTrue();

        await call.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Context_TracksCancelledCompletion()
    {
        var meta = new RequestMeta(service: "svc", procedure: "stream", transport: "http");
        var call = HttpStreamCall.CreateServerStream(meta);
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Cancelled, "cancelled", transport: "http");

        await call.CompleteAsync(error, TestContext.Current.CancellationToken);

        call.Context.CompletionStatus.ShouldBe(StreamCompletionStatus.Cancelled);
        call.Context.CompletionError.ShouldBeSameAs(error);

        await call.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask DisposeWithoutCompletion_MarksCancelled()
    {
        var meta = new RequestMeta(service: "svc", procedure: "stream", transport: "http");
        var call = HttpStreamCall.CreateServerStream(meta);

        await call.DisposeAsync();

        call.Context.CompletionStatus.ShouldBe(StreamCompletionStatus.Cancelled);
    }
}
