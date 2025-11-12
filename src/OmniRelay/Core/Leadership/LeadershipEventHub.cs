using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Core.Leadership;

/// <summary>Fan-out hub that multiplexes leadership events to SSE/gRPC subscribers.</summary>
public sealed class LeadershipEventHub
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private readonly ILogger<LeadershipEventHub> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _snapshotLock = new();
    private ImmutableDictionary<string, LeadershipToken> _tokens =
        [];
    private LeadershipSnapshot _snapshot;

    public LeadershipEventHub(ILogger<LeadershipEventHub> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _snapshot = new LeadershipSnapshot(_timeProvider.GetUtcNow(), []);
    }

    /// <summary>Returns the latest leadership snapshot.</summary>
    public LeadershipSnapshot Snapshot()
    {
        lock (_snapshotLock)
        {
            return _snapshot;
        }
    }

    /// <summary>Subscribes to leadership events filtered by optional scope id.</summary>
    public IAsyncEnumerable<LeadershipEvent> SubscribeAsync(string? scopeFilter, CancellationToken cancellationToken)
    {
        var subscription = new Subscription(scopeFilter, cancellationToken);
        if (!_subscriptions.TryAdd(subscription.Id, subscription))
        {
            subscription.Dispose();
            throw new InvalidOperationException("Failed to register leadership subscription.");
        }

        // Emit snapshot first so clients have immediate state.
        var snapshot = Snapshot();
        foreach (var snapshotEvent in snapshot.Tokens
            .Where(t => subscription.Matches(t.Scope))
            .Select(t => LeadershipEvent.ForSnapshot(t) with { OccurredAt = _timeProvider.GetUtcNow() }))
        {
            subscription.TryWrite(snapshotEvent);
        }

        return ReadAsync(subscription, cancellationToken);
    }

    /// <summary>Upserts the supplied leadership token and updates the cached snapshot.</summary>
    public void UpsertToken(LeadershipToken token)
    {
        lock (_snapshotLock)
        {
            _tokens = _tokens.SetItem(token.Scope, token);
            _snapshot = new LeadershipSnapshot(_timeProvider.GetUtcNow(), [.. _tokens.Values]);
        }
    }

    /// <summary>Removes scope metadata when leadership is lost.</summary>
    public void RemoveScope(string scope)
    {
        lock (_snapshotLock)
        {
            if (_tokens.ContainsKey(scope))
            {
                _tokens = _tokens.Remove(scope);
                _snapshot = new LeadershipSnapshot(_timeProvider.GetUtcNow(), [.. _tokens.Values]);
            }
        }
    }

    /// <summary>Broadcasts an event to all subscribers.</summary>
    public void Publish(LeadershipEvent leadershipEvent)
    {
        foreach (var subscription in _subscriptions.Values.Where(s => s.Matches(leadershipEvent.Scope)))
        {
            if (!subscription.TryWrite(leadershipEvent))
            {
                LeadershipEventHubLog.DroppedEvent(_logger, leadershipEvent.Scope);
            }
        }
    }

    private async IAsyncEnumerable<LeadershipEvent> ReadAsync(Subscription subscription, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var scopeGuard = subscription;

        try
        {
            while (await subscription.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (subscription.Reader.TryRead(out var evt))
                {
                    yield return evt;
                }
            }
        }
        finally
        {
            await scopeGuard.DisposeAsync().ConfigureAwait(false);
            _subscriptions.TryRemove(subscription.Id, out _);
        }
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly Channel<LeadershipEvent> _channel;
        private readonly CancellationTokenRegistration _registration;
        private bool _disposed;

        public Subscription(string? scopeFilter, CancellationToken cancellationToken)
        {
            Id = Guid.NewGuid();
            ScopeFilter = string.IsNullOrWhiteSpace(scopeFilter) ? null : scopeFilter!.Trim();
            _channel = Channel.CreateBounded<LeadershipEvent>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false
            });

            _registration = cancellationToken.Register(static state =>
            {
                var writer = (ChannelWriter<LeadershipEvent>)state!;
                writer.TryComplete();
            }, _channel.Writer);
        }

        public Guid Id { get; }

        public string? ScopeFilter { get; }

        public ChannelReader<LeadershipEvent> Reader => _channel.Reader;

        public bool Matches(string scope)
        {
            if (ScopeFilter is null)
            {
                return true;
            }

            return string.Equals(ScopeFilter, scope, StringComparison.OrdinalIgnoreCase);
        }

        public bool TryWrite(LeadershipEvent leadershipEvent) => _channel.Writer.TryWrite(leadershipEvent);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _registration.Dispose();
            _channel.Writer.TryComplete();
            _disposed = true;
        }
    }
}

internal static partial class LeadershipEventHubLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Dropping leadership event for scope {Scope} (subscriber backlog full).")]
    public static partial void DroppedEvent(ILogger logger, string scope);
}
