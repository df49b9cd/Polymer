using OmniRelay.Core.Leadership;
using Xunit;

namespace OmniRelay.Core.UnitTests.Leadership;

public sealed class LeadershipScopeTests
{
    [Fact]
    public void GlobalControl_HasExpectedValues()
    {
        var scope = LeadershipScope.GlobalControl;

        Assert.Equal("global-control", scope.ScopeId);
        Assert.Equal(LeadershipScopeKinds.Global, scope.ScopeKind);
        Assert.Empty(scope.Labels);
    }

    [Fact]
    public void ForShard_GeneratesCorrectScopeId()
    {
        var scope = LeadershipScope.ForShard("test-ns", "5");

        Assert.Equal("shard/test-ns/5", scope.ScopeId);
        Assert.Equal(LeadershipScopeKinds.Shard, scope.ScopeKind);
        Assert.Equal("test-ns", scope.Labels["namespace"]);
        Assert.Equal("5", scope.Labels["shardId"]);
    }

    [Fact]
    public void Create_WithCustomKind_CreatesCustomScope()
    {
        var scope = LeadershipScope.Create("custom-scope-id", LeadershipScopeKinds.Custom);

        Assert.Equal("custom-scope-id", scope.ScopeId);
        Assert.Equal(LeadershipScopeKinds.Custom, scope.ScopeKind);
    }

    [Fact]
    public void Create_WithLabels_PreservesLabels()
    {
        var labels = new Dictionary<string, string>
        {
            ["key1"] = "value1",
            ["key2"] = "value2"
        };

        var scope = LeadershipScope.Create("test", LeadershipScopeKinds.Custom, labels);

        Assert.Equal(2, scope.Labels.Count);
        Assert.Equal("value1", scope.Labels["key1"]);
        Assert.Equal("value2", scope.Labels["key2"]);
    }

    [Theory]
    [InlineData("global-control", "global-control")]
    [InlineData("shard/ns/1", "shard/ns/1")]
    [InlineData("custom-scope", "custom-scope")]
    public void TryParse_ValidInput_ReturnsTrue(string input, string expectedScopeId)
    {
        var result = LeadershipScope.TryParse(input, out var scope);

        Assert.True(result);
        Assert.Equal(expectedScopeId, scope.ScopeId);
    }

    [Fact]
    public void TryParse_EmptyString_ReturnsFalse()
    {
        var result = LeadershipScope.TryParse("", out var scope);

        Assert.False(result);
    }
}
