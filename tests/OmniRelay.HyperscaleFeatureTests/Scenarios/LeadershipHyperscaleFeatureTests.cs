using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Core.Leadership;
using OmniRelay.HyperscaleFeatureTests.Infrastructure;
using Xunit;
using static AwesomeAssertions.FluentActions;

namespace OmniRelay.HyperscaleFeatureTests.Scenarios;

public sealed class LeadershipHyperscaleFeatureTests : IAsyncLifetime
{
    private readonly LaggyLeadershipStore _store = new();
    private readonly HyperscaleLeadershipClusterOptions _options;
    private readonly HyperscaleLeadershipCluster _cluster;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Random _random = new(421);

    public LeadershipHyperscaleFeatureTests()
    {
        _loggerFactory = NullLoggerFactory.Instance;
        _options = new HyperscaleLeadershipClusterOptions
        {
            Regions = ["iad", "phx", "dub"],
            Namespaces = ["mesh.control", "mesh.telemetry"],
            NodesPerRegion = 3,
            ShardsPerNamespace = 4,
            LeaseDuration = TimeSpan.FromSeconds(4),
            RenewalLeadTime = TimeSpan.FromMilliseconds(800),
            EvaluationInterval = TimeSpan.FromMilliseconds(70),
            ElectionBackoff = TimeSpan.FromMilliseconds(250),
            MaxElectionWindow = TimeSpan.FromSeconds(5),
            ClusterId = "hyperscale-validation",
            MeshVersion = "mesh-leadership-feature-tests"
        };

        _cluster = new HyperscaleLeadershipCluster(_store, _options, _loggerFactory);
    }

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _cluster.StartAsync(ct);
        await _cluster.WaitForStableLeadershipAsync(TimeSpan.FromSeconds(15), ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _cluster.DisposeAsync();
    }

    [Fact(DisplayName = "Leadership cluster maintains exclusive leaders per scope and fails over inside SLA", Timeout = TestTimeouts.Default)]
    public async ValueTask LeadershipCluster_MeetsElectionSlaUnderChurnAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _cluster.WaitForStableLeadershipAsync(TimeSpan.FromSeconds(10), ct);

        var targetScopes = new List<string> { LeadershipScope.GlobalControl.ScopeId };
        targetScopes.AddRange(_cluster.Scopes
            .Where(scope => scope.ScopeKind == LeadershipScopeKinds.Shard)
            .Select(scope => scope.ScopeId)
            .Take(18));

        foreach (var scopeId in targetScopes)
        {
            var incumbent = _cluster.GetToken(scopeId);
            incumbent.Should().NotBeNull();

            var duration = await _cluster.ForceFailoverAsync(scopeId, ct);
            duration.Should().BeLessThanOrEqualTo(_options.MaxElectionWindow, $"Failover for {scopeId} exceeded SLA ({duration}).");

            var successor = _cluster.GetToken(scopeId);
            successor.Should().NotBeNull();
            successor!.LeaderId.Should().NotBe(incumbent!.LeaderId);
            successor.FenceToken.Should().BeGreaterThan(incumbent.FenceToken, "Fence token must increase after failover.");
        }

        var snapshot = _cluster.Snapshot();
        foreach (var scope in _cluster.Scopes)
        {
            var token = snapshot.Find(scope.ScopeId);
            token.Should().NotBeNull();
            token!.IsExpired(DateTimeOffset.UtcNow).Should().BeFalse($"Token for {scope.ScopeId} expired.");
        }
    }

    [Fact(DisplayName = "Leadership event streams stay consistent during watcher churn and registry lag", Timeout = TestTimeouts.Default)]
    public async ValueTask LeadershipStreams_WithWatcherChurnRemainConsistentAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _cluster.WaitForStableLeadershipAsync(TimeSpan.FromSeconds(10), ct);
        var observers = _cluster.Observers;
        var scopes = _cluster.Scopes.Select(scope => scope.ScopeId).ToArray();
        var shardScopes = _cluster.Scopes
            .Where(scope => scope.ScopeKind == LeadershipScopeKinds.Shard)
            .Select(scope => scope.ScopeId)
            .ToArray();

        var watchers = new List<LeadershipWatcher>();
        try
        {
            watchers.AddRange(CreateWatchers(observers, scopes, unfilteredCount: 32, scopedCount: 32, ct));
            await EnsureWatchersConvergedAsync(watchers, TimeSpan.FromSeconds(10), ct);

            _store.AdditionalLatency = TimeSpan.FromMilliseconds(200);
            _store.ConfigureFaultProbability(0.10);

            for (var iteration = 0; iteration < 3; iteration++)
            {
                var scopeId = shardScopes[(iteration * 7) % shardScopes.Length];
                await _cluster.ForceFailoverAsync(scopeId, ct);

                await ChurnWatchersAsync(watchers, observers, scopes, replacements: watchers.Count / 3, ct);
                await EnsureWatchersConvergedAsync(watchers, TimeSpan.FromSeconds(15), ct, [scopeId]);
            }
        }
        finally
        {
            _store.AdditionalLatency = TimeSpan.Zero;
            _store.ConfigureFaultProbability(0);
            foreach (var watcher in watchers)
            {
                await watcher.DisposeAsync();
            }
        }

        watchers.Should().Contain(watcher => watcher.Transport.Contains("h3", StringComparison.OrdinalIgnoreCase));
        watchers.Should().Contain(watcher => watcher.Transport.Contains("h2", StringComparison.OrdinalIgnoreCase));
    }

    private List<LeadershipWatcher> CreateWatchers(
        IReadOnlyList<ILeadershipObserver> observers,
        IReadOnlyList<string> scopeIds,
        int unfilteredCount,
        int scopedCount,
        CancellationToken cancellationToken)
    {
        var watchers = new List<LeadershipWatcher>(unfilteredCount + scopedCount);

        for (var i = 0; i < unfilteredCount; i++)
        {
            var observer = observers[_random.Next(observers.Count)];
            var transport = i % 2 == 0 ? "http/3" : "http/2";
            watchers.Add(new LeadershipWatcher(observer, scopeFilter: null, transport, cancellationToken));
        }

        for (var i = 0; i < scopedCount; i++)
        {
            var observer = observers[_random.Next(observers.Count)];
            var scopeFilter = scopeIds[_random.Next(scopeIds.Count)];
            var transport = i % 2 == 0 ? "grpc-h3" : "grpc-h2";
            watchers.Add(new LeadershipWatcher(observer, scopeFilter, transport, cancellationToken));
        }

        return watchers;
    }

    private async Task ChurnWatchersAsync(
        List<LeadershipWatcher> watchers,
        IReadOnlyList<ILeadershipObserver> observers,
        IReadOnlyList<string> scopeIds,
        int replacements,
        CancellationToken cancellationToken)
    {
        var actual = Math.Min(replacements, watchers.Count);
        for (var i = 0; i < actual; i++)
        {
            var target = watchers[_random.Next(watchers.Count)];
            watchers.Remove(target);
            await target.DisposeAsync();

            var scopeFilter = _random.NextDouble() > 0.5
                ? scopeIds[_random.Next(scopeIds.Count)]
                : null;
            var observer = observers[_random.Next(observers.Count)];
            var transport = scopeFilter is null
                ? (_random.NextDouble() > 0.5 ? "http/3" : "http/2")
                : (_random.NextDouble() > 0.5 ? "grpc-h3" : "grpc-h2");
            watchers.Add(new LeadershipWatcher(observer, scopeFilter, transport, cancellationToken));
        }
    }

    private async Task EnsureWatchersConvergedAsync(
        List<LeadershipWatcher> watchers,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? scopeFilter = null)
    {
        string? lastFailure = null;
        var converged = await WaitForConditionAsync(() =>
        {
            var snapshot = _cluster.Snapshot();
            var scopes = scopeFilter is null
                ? _cluster.Scopes
                : _cluster.Scopes.Where(scope => scopeFilter.Contains(scope.ScopeId, StringComparer.OrdinalIgnoreCase)).ToArray();
            var result = WatchersMatchSnapshot(watchers, snapshot, scopes, out var failure);
            if (!result)
            {
                lastFailure = failure;
            }

            return result;
        }, timeout, cancellationToken);

        converged.Should().BeTrue(lastFailure ?? "Leadership watchers failed to catch up with the latest snapshot.");
    }

    private static bool WatchersMatchSnapshot(
        IEnumerable<LeadershipWatcher> watchers,
        LeadershipSnapshot snapshot,
        IReadOnlyList<LeadershipScope> scopes,
        out string? failureReason)
    {
        var scopeLookup = scopes.ToDictionary(scope => scope.ScopeId, scope => snapshot.Find(scope.ScopeId), StringComparer.OrdinalIgnoreCase);
        if (scopeLookup.Count == 0)
        {
            failureReason = "No scopes were selected for validation.";
            return false;
        }

        var missingTokens = scopeLookup.Where(entry => entry.Value is null).Select(entry => entry.Key).ToArray();
        if (missingTokens.Length > 0)
        {
            failureReason = $"Snapshot missing scopes: {string.Join(", ", missingTokens)}";
            return false;
        }

        var unfilteredWatchers = watchers.Where(watcher => watcher.ScopeFilter is null).ToArray();
        if (unfilteredWatchers.Length == 0)
        {
            failureReason = "No SSE watchers available.";
            return false;
        }

        var coveredScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var watcher in unfilteredWatchers)
        {
            foreach (var scope in scopes)
            {
                var expected = scopeLookup[scope.ScopeId]!;
                if (watcher.TryGetToken(scope.ScopeId, out var token) &&
                    token!.FenceToken == expected.FenceToken)
                {
                    coveredScopes.Add(scope.ScopeId);
                }
            }
        }

        if (coveredScopes.Count < scopes.Count)
        {
            var uncovered = scopes.Where(scope => !coveredScopes.Contains(scope.ScopeId)).Select(scope => scope.ScopeId);
            failureReason = $"SSE coverage missing: {string.Join(", ", uncovered)}";
            return false;
        }

        foreach (var watcher in watchers.Where(w => w.ScopeFilter is not null))
        {
            var scopeId = watcher.ScopeFilter!;
            if (!scopeLookup.TryGetValue(scopeId, out var expected))
            {
                continue;
            }

            if (expected is null)
            {
                failureReason = $"Watcher missing scope {scopeId}.";
                return false;
            }

            if (!watcher.TryGetToken(scopeId, out var token) || token!.FenceToken != expected.FenceToken)
            {
                failureReason = $"Watcher stale for scope {scopeId}.";
                return false;
            }
        }

        failureReason = null;
        return true;
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        return predicate();
    }

    private sealed class LeadershipWatcher : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _readerTask;
        private readonly ConcurrentDictionary<string, LeadershipToken> _tokens = new(StringComparer.OrdinalIgnoreCase);

        public LeadershipWatcher(
            ILeadershipObserver observer,
            string? scopeFilter,
            string transport,
            CancellationToken cancellationToken)
        {
            ScopeFilter = scopeFilter;
            Transport = transport;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            _readerTask = Task.Run(async () =>
            {
                await foreach (var evt in observer.SubscribeAsync(scopeFilter, linked.Token))
                {
                    if (evt.Token is null)
                    {
                        continue;
                    }

                    _tokens.AddOrUpdate(evt.Scope, evt.Token, (_, _) => evt.Token);
                }
            });
        }

        public string? ScopeFilter { get; }

        public string Transport { get; }

        public bool TryGetToken(string scopeId, [NotNullWhen(true)] out LeadershipToken? token) =>
            _tokens.TryGetValue(scopeId, out token);

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _readerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when shutting down watchers.
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }
}
