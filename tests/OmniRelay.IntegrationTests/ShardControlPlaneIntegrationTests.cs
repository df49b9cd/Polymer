using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniRelay.Core.Shards;
using OmniRelay.Core.Shards.ControlPlane;
using OmniRelay.Tests.Support;
using Xunit;

namespace OmniRelay.IntegrationTests;

public sealed class ShardControlPlaneIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async ValueTask ListShards_WithScopeReturnsResults()
    {
        await using var host = await ShardControlPlaneTestHost.StartAsync(TestContext.Current.CancellationToken);
        await SeedAsync(host);

        using var client = new HttpClient { BaseAddress = host.BaseAddress };
        client.DefaultRequestHeaders.Add("x-mesh-scope", "mesh.read");

        var response = await client.GetAsync("/control/shards?namespace=mesh.integration", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync(ShardJsonContext.Default.ShardListResponse, TestContext.Current.CancellationToken);
        payload.ShouldNotBeNull();
        var list = payload!;
        list.Items.ShouldNotBeEmpty();
        list.Items.ShouldAllBe(item => item.Namespace == "mesh.integration");
    }

    [Fact(Timeout = 20_000)]
    public async ValueTask ListShards_WithoutScopeIsForbidden()
    {
        await using var host = await ShardControlPlaneTestHost.StartAsync(TestContext.Current.CancellationToken);
        await SeedAsync(host);

        using var client = new HttpClient { BaseAddress = host.BaseAddress };
        var response = await client.GetAsync("/control/shards", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask SimulateShards_ReturnsPlan()
    {
        await using var host = await ShardControlPlaneTestHost.StartAsync(TestContext.Current.CancellationToken);
        await SeedAsync(host);

        using var client = new HttpClient { BaseAddress = host.BaseAddress };
        client.DefaultRequestHeaders.Add("x-mesh-scope", "mesh.operate");

        var request = new ShardSimulationRequest
        {
            Namespace = "mesh.integration",
            StrategyId = "rendezvous",
            Nodes = new[]
            {
                new ShardSimulationNode("node-a", 1.0, "iad", "iad-1"),
                new ShardSimulationNode("node-b", 1.1, "iad", "iad-2")
            }
        };

        var response = await client.PostAsJsonAsync("/control/shards/simulate", request, ShardJsonContext.Default.ShardSimulationRequest, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync(ShardJsonContext.Default.ShardSimulationResponse, TestContext.Current.CancellationToken);
        payload.ShouldNotBeNull();
        var simulation = payload!;
        simulation.Namespace.ShouldBe("mesh.integration");
        simulation.Assignments.ShouldNotBeEmpty();
    }

    private static ShardMutationRequest CreateMutation(string ns, string shard, string owner, int version = 0)
    {
        return new ShardMutationRequest
        {
            Namespace = ns,
            ShardId = shard,
            StrategyId = "rendezvous",
            OwnerNodeId = owner,
            LeaderId = owner,
            CapacityHint = 1,
            Status = ShardStatus.Active,
            ExpectedVersion = version > 0 ? version : null,
            ChangeTicket = $"chg-{ns}-{shard}",
            ChangeMetadata = new ShardChangeMetadata("integration", "seed", $"ticket-{shard}")
        };
    }

    private static async Task SeedAsync(ShardControlPlaneTestHost host)
    {
        var seeds = Enumerable.Range(0, 4)
            .Select(i => CreateMutation("mesh.integration", $"shard-{i:D3}", "node-a"));
        await host.SeedAsync(seeds);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ShardListResponse))]
[JsonSerializable(typeof(ShardSimulationResponse))]
[JsonSerializable(typeof(ShardSimulationRequest))]
internal partial class ShardJsonContext : JsonSerializerContext;
