using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace OmniRelay.Core.Shards.Hashing;

/// <summary>Registry for shard hashing strategies so namespaces can select algorithms by id.</summary>
public sealed class ShardHashStrategyRegistry
{
    private readonly ConcurrentDictionary<string, IShardHashStrategy> _strategies =
        new(StringComparer.OrdinalIgnoreCase);

    public ShardHashStrategyRegistry(IEnumerable<IShardHashStrategy>? strategies = null)
    {
        Register(new RingShardHashStrategy());
        Register(new RendezvousShardHashStrategy());
        Register(new LocalityAwareShardHashStrategy());

        if (strategies is null)
        {
            return;
        }

        foreach (var strategy in strategies)
        {
            Register(strategy, overwrite: true);
        }
    }

    public void Register(IShardHashStrategy strategy, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        var addResult = _strategies.TryAdd(strategy.Id, strategy);
        if (!addResult && overwrite)
        {
            _strategies[strategy.Id] = strategy;
            return;
        }

        if (!addResult)
        {
            throw new InvalidOperationException($"Shard hash strategy '{strategy.Id}' is already registered.");
        }
    }

    public bool TryGet(string strategyId, [NotNullWhen(true)] out IShardHashStrategy? strategy)
    {
        if (string.IsNullOrWhiteSpace(strategyId))
        {
            strategy = null;
            return false;
        }

        return _strategies.TryGetValue(strategyId, out strategy);
    }

    public IShardHashStrategy Resolve(string strategyId)
    {
        if (!TryGet(strategyId, out var strategy))
        {
            throw new InvalidOperationException($"Unknown shard hash strategy '{strategyId}'.");
        }

        return strategy;
    }

    public ShardHashPlan Compute(string strategyId, ShardHashRequest request)
    {
        var strategy = Resolve(strategyId);
        return strategy.Compute(request);
    }

    public IEnumerable<string> RegisteredStrategyIds => _strategies.Keys;
}
