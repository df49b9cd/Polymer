using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Core.Middleware;

public sealed class DeadlineMiddlewareTests
{
    [Fact]
    public async Task UnaryOutbound_DeadlineAlreadyExceeded_ReturnsErrorWithoutInvokingNext()
    {
        var middleware = new DeadlineMiddleware();
        var meta = new RequestMeta(
            service: "svc",
            procedure: "echo::call",
            transport: "grpc",
            timeToLive: TimeSpan.FromMilliseconds(-1));
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var invoked = false;

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryOutboundHandler)((req, token) =>
            {
                invoked = true;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            }));

        Assert.True(result.IsFailure);
        Assert.False(invoked);
        Assert.Equal(OmniRelayStatusCode.DeadlineExceeded, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }

    [Fact]
    public async Task UnaryInbound_CancellationTriggeredByDeadline_MapsToDeadlineExceeded()
    {
        var middleware = new DeadlineMiddleware();
        var meta = new RequestMeta(
            service: "svc",
            procedure: "echo::call",
            transport: "grpc",
            timeToLive: TimeSpan.FromMilliseconds(30));
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryInboundHandler)(async (req, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty));
            }));

        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.DeadlineExceeded, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }

    [Fact]
    public async Task MinimumLeadTime_TooClose_ReturnsDeadlineExceeded()
    {
        var options = new DeadlineOptions { MinimumLeadTime = TimeSpan.FromMilliseconds(100) };
        var middleware = new DeadlineMiddleware(options);
        var meta = new RequestMeta(
            service: "svc",
            procedure: "echo::call",
            transport: "grpc",
            timeToLive: TimeSpan.FromMilliseconds(50));
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var invoked = false;

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryOutboundHandler)((req, token) =>
            {
                invoked = true;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            }));

        Assert.True(result.IsFailure);
        Assert.False(invoked);
        Assert.Equal(OmniRelayStatusCode.DeadlineExceeded, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }

    [Fact]
    public async Task UnaryOutbound_NoDeadline_PropagatesCall()
    {
        var middleware = new DeadlineMiddleware();
        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var invoked = false;

        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryOutboundHandler)((req, token) =>
            {
                invoked = true;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            }));

        Assert.True(result.IsSuccess);
        Assert.True(invoked);
    }
}
