using System.Threading.Channels;
using Xunit;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;

namespace OmniRelay.Tests.Core;

public class DuplexStreamCallTests
{
    [Fact]
    public async Task Create_WiresBidirectionalChannels()
    {
        var meta = new RequestMeta(service: "svc", procedure: "echo");
        var call = DuplexStreamCall.Create(meta);

        await call.RequestWriter.WriteAsync(new byte[] { 0x01 }, TestContext.Current.CancellationToken);
        await call.ResponseWriter.WriteAsync(new byte[] { 0x02 }, TestContext.Current.CancellationToken);

        Assert.True(call.RequestReader.TryRead(out var requestPayload));
        Assert.Equal(new byte[] { 0x01 }, requestPayload.ToArray());

        Assert.True(call.ResponseReader.TryRead(out var responsePayload));
        Assert.Equal(new byte[] { 0x02 }, responsePayload.ToArray());

        Assert.Equal(1, call.Context.RequestMessageCount);
        Assert.Equal(1, call.Context.ResponseMessageCount);

        await call.DisposeAsync();
    }

    [Fact]
    public async Task Context_TracksCountsAndCompletions()
    {
        var meta = new RequestMeta(service: "svc", procedure: "echo", transport: "test");
        var call = DuplexStreamCall.Create(meta);

        await call.RequestWriter.WriteAsync(new byte[] { 0x01 }, TestContext.Current.CancellationToken);
        await call.RequestWriter.WriteAsync(new byte[] { 0x02 }, TestContext.Current.CancellationToken);
        await call.ResponseWriter.WriteAsync(new byte[] { 0x11 }, TestContext.Current.CancellationToken);

        Assert.Equal(2, call.Context.RequestMessageCount);
        Assert.Equal(1, call.Context.ResponseMessageCount);

        await call.CompleteRequestsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await call.CompleteResponsesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(StreamCompletionStatus.Succeeded, call.Context.RequestCompletionStatus);
        Assert.Equal(StreamCompletionStatus.Succeeded, call.Context.ResponseCompletionStatus);
        Assert.Null(call.Context.RequestCompletionError);
        Assert.Null(call.Context.ResponseCompletionError);
        Assert.True(call.Context.RequestCompletedAtUtc.HasValue);
        Assert.True(call.Context.ResponseCompletedAtUtc.HasValue);

        await call.DisposeAsync();
    }

    [Fact]
    public async Task CompleteResponsesAsync_WithErrorPropagatesException()
    {
        var meta = new RequestMeta(service: "svc", procedure: "echo", transport: "test");
        var call = DuplexStreamCall.Create(meta);
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, "boom", transport: "test");

        await call.CompleteResponsesAsync(error, TestContext.Current.CancellationToken);

        var readTask = call.ResponseReader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        var channelException = await Assert.ThrowsAsync<ChannelClosedException>(() => readTask);
        var polymerException = Assert.IsType<OmniRelayException>(channelException.InnerException);
        Assert.Equal(OmniRelayStatusCode.Internal, polymerException.StatusCode);

        Assert.Equal(StreamCompletionStatus.Faulted, call.Context.ResponseCompletionStatus);
        Assert.Same(error, call.Context.ResponseCompletionError);

        await call.DisposeAsync();
    }

    [Fact]
    public async Task CompleteRequestsAsync_WithCancelledErrorSetsCancelledStatus()
    {
        var meta = new RequestMeta(service: "svc", procedure: "echo", transport: "test");
        var call = DuplexStreamCall.Create(meta);
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Cancelled, "cancel", transport: "test");

        await call.CompleteRequestsAsync(error, TestContext.Current.CancellationToken);

        Assert.Equal(StreamCompletionStatus.Cancelled, call.Context.RequestCompletionStatus);
        Assert.Same(error, call.Context.RequestCompletionError);

        await call.DisposeAsync();
    }
}
