using OmniRelay.Core.Shards;
using OmniRelay.FeatureTests.Fixtures;
using OmniRelay.Tests.Support;
using Xunit;

namespace OmniRelay.FeatureTests.Features;

public sealed class ShardControlFeatureTests : IAsyncLifetime
{
    private ShardControlPlaneTestHost? _host;

    public async ValueTask InitializeAsync()
    {
        _host = await ShardControlPlaneTestHost.StartAsync(TestContext.Current.CancellationToken);
        await SeedAsync(_host);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async ValueTask MeshShardsCommands_RunAgainstControlPlane()
    {
        var baseUrl = _host!.BaseAddress.ToString();
        var ct = TestContext.Current.CancellationToken;

        var listResult = await CliCommandRunner.RunAsync($"mesh shards list --url {baseUrl} --namespace mesh.feature --json", ct);
        listResult.ExitCode.ShouldBe(0);
        listResult.Stdout.ShouldContain("mesh.feature");

        var diffResult = await CliCommandRunner.RunAsync($"mesh shards diff --url {baseUrl} --from-version 0 --to-version 10 --json", ct);
        diffResult.ExitCode.ShouldBe(0);
        diffResult.Stdout.ShouldContain("shard-000");

        var simulateResult = await CliCommandRunner.RunAsync($"mesh shards simulate --url {baseUrl} --namespace mesh.feature --node node-a:1.0 --node node-b:0.8", ct);
        simulateResult.ExitCode.ShouldBe(0);
        simulateResult.Stdout.ShouldContain("Changes");
    }

    private static async Task SeedAsync(ShardControlPlaneTestHost host)
    {
        var mutations = Enumerable.Range(0, 4)
            .Select(i => new ShardMutationRequest
            {
                Namespace = "mesh.feature",
                ShardId = $"shard-{i:D3}",
                StrategyId = "rendezvous",
                OwnerNodeId = i % 2 == 0 ? "node-a" : "node-b",
                LeaderId = i % 2 == 0 ? "node-a" : "node-b",
                CapacityHint = 1,
                Status = ShardStatus.Active,
                ChangeTicket = $"chg-feature-{i:D3}",
                ChangeMetadata = new ShardChangeMetadata("feature", "seed", $"chg-feature-{i:D3}")
            });

        await host.SeedAsync(mutations);
    }
}
