using System.Text;
using Microsoft.Extensions.Configuration;
using OmniRelay.Configuration.Models;
using OmniRelay.Configuration.Sharding;
using OmniRelay.Core.Shards.Hashing;
using Shouldly;
using Xunit;

namespace OmniRelay.Configuration.UnitTests.Configuration;

public sealed class ShardingConfigurationExtensionsTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void ShardingSection_BindsAndComputesPlan()
    {
        const string json = """
        {
          "service": "mesh.control",
          "sharding": {
            "namespaces": [
              {
                "namespace": "mesh.control",
                "strategy": "ring",
                "capacityHint": 2,
                "nodes": [
                  { "nodeId": "iad-zone-1", "region": "iad", "zone": "iad-1" },
                  { "nodeId": "phx-zone-1", "region": "phx", "zone": "phx-1", "weight": 2 }
                ],
                "shards": [
                  { "shardId": "iad-1-0", "localityHint": "iad/iad-1" },
                  { "shardId": "phx-1-0", "localityHint": "phx/phx-1" },
                  { "shardId": "global-0" }
                ]
              }
            ]
          }
        }
        """;

        using var jsonStream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(jsonStream).Build();
        var options = configuration.Get<OmniRelayConfigurationOptions>();
        options.ShouldNotBeNull();
        options.Sharding.Namespaces.Count.ShouldBe(1);

        var @namespace = options.Sharding.Namespaces[0];
        @namespace.Namespace.ShouldBe("mesh.control");
        @namespace.Strategy.ShouldBe("ring");
        @namespace.Nodes.Count.ShouldBe(2);
        @namespace.Shards.Count.ShouldBe(3);

        var request = @namespace.ToHashRequest();
        request.Namespace.ShouldBe("mesh.control");
        request.Nodes.Count.ShouldBe(2);
        request.Shards.Count.ShouldBe(3);

        var registry = new ShardHashStrategyRegistry();
        var plan = @namespace.ComputePlan(registry);
        plan.Assignments.Count.ShouldBe(3);
        plan.StrategyId.ShouldBe(ShardHashStrategyIds.ConsistentRing);
    }
}
