using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OmniRelay.Core;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public sealed class ResourceLeaseBackpressureListenerTests
{
    private static ResourceLeaseBackpressureSignal ActiveSignal(long pending = 10) =>
        new(true, pending, DateTimeOffset.UtcNow, HighWatermark: 8, LowWatermark: 4);

    private static ResourceLeaseBackpressureSignal ClearedSignal(long pending = 2) =>
        new(false, pending, DateTimeOffset.UtcNow, HighWatermark: 8, LowWatermark: 4);

    [Fact]
    public async Task RateLimitingListener_TogglesGate()
    {
        await using var normal = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 16,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });

        await using var throttled = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 2,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });

        var gate = new BackpressureAwareRateLimiter(normalLimiter: normal, backpressureLimiter: throttled);
        var logger = Substitute.For<ILogger>();
        var listener = new RateLimitingBackpressureListener(gate, logger);
        var meta = new RequestMeta(service: "svc", transport: "http");

        Assert.Same(normal, gate.SelectLimiter(meta));

        await listener.OnBackpressureChanged(ActiveSignal(), CancellationToken.None);
        Assert.Same(throttled, gate.SelectLimiter(meta));

        await listener.OnBackpressureChanged(ClearedSignal(), CancellationToken.None);
        Assert.Same(normal, gate.SelectLimiter(meta));
    }

    [Fact]
    public async Task DiagnosticsListener_StoresLatestAndStreams()
    {
        var listener = new ResourceLeaseBackpressureDiagnosticsListener(historyCapacity: 4);
        Assert.Null(listener.Latest);

        var signal = ActiveSignal(pending: 42);
        await listener.OnBackpressureChanged(signal, CancellationToken.None);

        Assert.Equal(signal, listener.Latest);

        var read = await listener.Updates.ReadAsync(CancellationToken.None);
        Assert.Equal(signal, read);

        var cleared = ClearedSignal();
        await listener.OnBackpressureChanged(cleared, CancellationToken.None);
        Assert.Equal(cleared, listener.Latest);

        Assert.True(listener.Updates.TryRead(out var clearedRead));
        Assert.Equal(cleared, clearedRead);
    }
}
