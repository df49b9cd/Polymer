using Xunit;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;

namespace OmniRelay.Tests.Transport;

public class HttpStreamCallTests
{
    [Fact]
    public async Task Context_TracksMessagesAndSuccessCompletion()
    {
        var meta = new RequestMeta(service: "svc", procedure: "stream", transport: "http");
        var call = HttpStreamCall.CreateServerStream(meta);

        await call.WriteAsync(new byte[] { 0x01 }, TestContext.Current.CancellationToken);
        await call.WriteAsync(new byte[] { 0x02 }, TestContext.Current.CancellationToken);

        Assert.Equal(2, call.Context.MessageCount);

        await call.CompleteAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(StreamCompletionStatus.Succeeded, call.Context.CompletionStatus);
        Assert.Null(call.Context.CompletionError);
        Assert.True(call.Context.CompletedAtUtc.HasValue);

        await call.DisposeAsync();
    }

    [Fact]
    public async Task Context_TracksCancelledCompletion()
    {
        var meta = new RequestMeta(service: "svc", procedure: "stream", transport: "http");
        var call = HttpStreamCall.CreateServerStream(meta);
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Cancelled, "cancelled", transport: "http");

        await call.CompleteAsync(error, TestContext.Current.CancellationToken);

        Assert.Equal(StreamCompletionStatus.Cancelled, call.Context.CompletionStatus);
        Assert.Same(error, call.Context.CompletionError);

        await call.DisposeAsync();
    }

    [Fact]
    public async Task DisposeWithoutCompletion_MarksCancelled()
    {
        var meta = new RequestMeta(service: "svc", procedure: "stream", transport: "http");
        var call = HttpStreamCall.CreateServerStream(meta);

        await call.DisposeAsync();

        Assert.Equal(StreamCompletionStatus.Cancelled, call.Context.CompletionStatus);
    }
}
