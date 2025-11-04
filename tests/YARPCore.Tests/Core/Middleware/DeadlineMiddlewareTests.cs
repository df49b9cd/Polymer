using Xunit;
using YARPCore.Core;
using YARPCore.Core.Middleware;
using YARPCore.Core.Transport;
using YARPCore.Errors;

using static Hugo.Go;

namespace YARPCore.Tests.Core.Middleware;

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
            (UnaryOutboundDelegate)((req, token) =>
            {
                invoked = true;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            }));

        Assert.True(result.IsFailure);
        Assert.False(invoked);
        Assert.Equal(PolymerStatusCode.DeadlineExceeded, PolymerErrorAdapter.ToStatus(result.Error!));
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
            (UnaryInboundDelegate)(async (req, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty));
            }));

        Assert.True(result.IsFailure);
        Assert.Equal(PolymerStatusCode.DeadlineExceeded, PolymerErrorAdapter.ToStatus(result.Error!));
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
            (UnaryOutboundDelegate)((req, token) =>
            {
                invoked = true;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            }));

        Assert.True(result.IsFailure);
        Assert.False(invoked);
        Assert.Equal(PolymerStatusCode.DeadlineExceeded, PolymerErrorAdapter.ToStatus(result.Error!));
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
            (UnaryOutboundDelegate)((req, token) =>
            {
                invoked = true;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            }));

        Assert.True(result.IsSuccess);
        Assert.True(invoked);
    }
}
