using System.Collections.Concurrent;
using System.Threading.Channels;
using Hugo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;

namespace OmniRelay.ControlPlane.ControlProtocol;

public interface IControlPlaneUpdatePublisher
{
    ValueTask<Result<Unit>> PublishAsync(ControlPlaneUpdate update, CancellationToken cancellationToken = default);
}

public interface IControlPlaneUpdateSource
{
    Result<ControlPlaneUpdate> Current { get; }
    ValueTask<Result<ControlPlaneSubscription>> SubscribeAsync(CancellationToken cancellationToken = default);
}

internal sealed partial class ControlPlaneUpdateStream : IControlPlaneUpdatePublisher, IControlPlaneUpdateSource, IDisposable
{
    private readonly ConcurrentDictionary<long, Channel<ControlPlaneUpdate>> _subscribers = new();
    private readonly ILogger<ControlPlaneUpdateStream> _logger;
    private readonly ControlProtocolOptions _options;
    private ControlPlaneUpdate _current;
    private long _nextId;
    private bool _disposed;

    public ControlPlaneUpdateStream(IOptions<ControlProtocolOptions> options, ILogger<ControlPlaneUpdateStream> logger)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _current = ControlPlaneUpdate.Empty;
    }

    public Result<ControlPlaneUpdate> Current => _disposed
        ? Err<ControlPlaneUpdate>(Error.From("Control-plane update stream disposed.", ControlProtocolErrors.UpdateStreamDisposedCode))
        : Ok(_current);

    public ValueTask<Result<ControlPlaneSubscription>> SubscribeAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return ValueTask.FromResult(Err<ControlPlaneSubscription>(Error.From("Control-plane update stream disposed.", ControlProtocolErrors.UpdateStreamDisposedCode)));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(Err<ControlPlaneSubscription>(Error.Canceled("Subscription canceled", cancellationToken)));
        }

        var channel = MakeChannel<ControlPlaneUpdate>(new BoundedChannelOptions(_options.SubscriberBufferCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var id = Interlocked.Increment(ref _nextId);
        _subscribers.TryAdd(id, channel);
        return ValueTask.FromResult(Ok(new ControlPlaneSubscription(id, channel.Reader, this)));
    }

    public ValueTask<Result<Unit>> PublishAsync(ControlPlaneUpdate update, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return ValueTask.FromResult(Err<Unit>(Error.From("Control-plane update stream disposed.", ControlProtocolErrors.UpdateStreamDisposedCode)));
        }

        ArgumentNullException.ThrowIfNull(update);
        _current = update;

        foreach (var (id, channel) in _subscribers)
        {
            if (!channel.Writer.TryWrite(update))
            {
                ControlPlaneUpdateStreamLog.SubscriptionDropped(_logger, id);
            }
        }

        return ValueTask.FromResult(Ok(Unit.Value));
    }

    internal void Remove(long id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var id in _subscribers.Keys)
        {
            Remove(id);
        }
    }

    private static partial class ControlPlaneUpdateStreamLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Control-plane update dropped for subscriber {SubscriberId} (channel full).")]
        public static partial void SubscriptionDropped(ILogger logger, long subscriberId);
    }
}

public sealed class ControlPlaneSubscription : IAsyncDisposable
{
    internal ControlPlaneSubscription(long id, ChannelReader<ControlPlaneUpdate> reader, ControlPlaneUpdateStream owner)
    {
        Id = id;
        Reader = reader;
        Owner = owner;
    }

    internal long Id { get; }
    internal ChannelReader<ControlPlaneUpdate> Reader { get; }
    private ControlPlaneUpdateStream Owner { get; }

    public ValueTask DisposeAsync()
    {
        Owner.Remove(Id);
        return ValueTask.CompletedTask;
    }
}
