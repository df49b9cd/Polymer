using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniRelay.Core.Shards;
using OmniRelay.Core.Shards.ControlPlane;
using OmniRelay.Tests.Support;
using Xunit;

namespace OmniRelay.HyperscaleFeatureTests.Scenarios;

public sealed class ShardControlHyperscaleFeatureTests : IAsyncLifetime
{
    private ShardControlPlaneTestHost? _host;

    public async ValueTask InitializeAsync()
    {
        _host = await ShardControlPlaneTestHost.StartAsync(TestContext.Current.CancellationToken);
        var seeds = Enumerable.Range(0, 2000)
            .Select(i => new ShardMutationRequest
            {
                Namespace = "mesh.hyperscale",
                ShardId = $"shard-{i:D4}",
                StrategyId = "rendezvous",
                OwnerNodeId = (i % 4) switch
                {
                    0 => "node-a",
                    1 => "node-b",
                    2 => "node-c",
                    _ => "node-d"
                },
                LeaderId = "node-a",
                CapacityHint = 1,
                Status = ShardStatus.Active,
                ChangeTicket = $"chg-hyperscale-{i:D4}",
                ChangeMetadata = new ShardChangeMetadata("hyperscale", "seed", $"chg-hyperscale-{i:D4}")
            });
        await _host.SeedAsync(seeds);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    [Fact(Timeout = 90_000)]
    public async ValueTask ListShards_SupportsThousandScalePagination()
    {
        using var client = new HttpClient { BaseAddress = _host!.BaseAddress };
        client.DefaultRequestHeaders.Add("x-mesh-scope", "mesh.read");

        var total = 0;
        string? cursor = null;
        do
        {
            var path = string.IsNullOrWhiteSpace(cursor)
                ? "/control/shards?namespace=mesh.hyperscale&pageSize=500"
                : $"/control/shards?namespace=mesh.hyperscale&pageSize=500&cursor={cursor}";
            var response = await client.GetFromJsonAsync(path, HyperscaleShardJsonContext.Default.ShardListResponse, TestContext.Current.CancellationToken);
            response.ShouldNotBeNull();
            var page = response!;
            total += page.Items.Count;
            cursor = page.NextCursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        total.ShouldBe(2000);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ShardListResponse))]
internal partial class HyperscaleShardJsonContext : JsonSerializerContext;
