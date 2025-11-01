using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Errors;
using Xunit;

namespace Polymer.Tests.Core;

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

        await call.DisposeAsync();
    }

    [Fact]
    public async Task CompleteResponsesAsync_WithErrorPropagatesException()
    {
        var meta = new RequestMeta(service: "svc", procedure: "echo", transport: "test");
        var call = DuplexStreamCall.Create(meta);
        var error = PolymerErrorAdapter.FromStatus(PolymerStatusCode.Internal, "boom", transport: "test");

        await call.CompleteResponsesAsync(error, TestContext.Current.CancellationToken);

        var readTask = call.ResponseReader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        var channelException = await Assert.ThrowsAsync<ChannelClosedException>(() => readTask);
        var polymerException = Assert.IsType<PolymerException>(channelException.InnerException);
        Assert.Equal(PolymerStatusCode.Internal, polymerException.StatusCode);

        await call.DisposeAsync();
    }
}
