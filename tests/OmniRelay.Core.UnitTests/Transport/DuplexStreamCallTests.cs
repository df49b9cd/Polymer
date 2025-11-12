using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using OmniRelay.Core;
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
        Assert.Equal(StreamCompletionStatus.Cancelled, call.Context.RequestCompletionStatus);
        Assert.NotNull(call.Context.RequestCompletedAtUtc);

        await call.CompleteResponsesAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(StreamCompletionStatus.Succeeded, call.Context.ResponseCompletionStatus);
        Assert.NotNull(call.Context.ResponseCompletedAtUtc);

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

        Assert.Equal(StreamCompletionStatus.Cancelled, call.Context.ResponseCompletionStatus);

        var closed = await Assert.ThrowsAsync<ChannelClosedException>(async () =>
            await call.ResponseReader.ReadAsync(TestContext.Current.CancellationToken));
        var relayException = Assert.IsType<OmniRelayException>(closed.InnerException);
        Assert.Equal("The response stream was cancelled.", relayException.Message);

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

        Assert.Equal(StreamCompletionStatus.Cancelled, call.Context.RequestCompletionStatus);

        var closed = await Assert.ThrowsAsync<ChannelClosedException>(async () =>
            await call.RequestReader.ReadAsync(TestContext.Current.CancellationToken));
        var relayException = Assert.IsType<OmniRelayException>(closed.InnerException);
        Assert.Equal("The request stream was cancelled.", relayException.Message);

        await call.DisposeAsync();
    }
}
