using OmniRelay.Core.Leadership;
using Xunit;

namespace OmniRelay.Core.UnitTests.Leadership;

public sealed class LeadershipScopeTests
{
    [Fact]
    public void GlobalControl_HasExpectedValues()
    {
        var scope = LeadershipScope.GlobalControl;

        scope.ScopeId.ShouldBe("global-control");
        scope.ScopeKind.ShouldBe(LeadershipScopeKinds.Global);
        scope.Labels.ShouldBeEmpty();
    }

    [Fact]
    public void ForShard_GeneratesCorrectScopeId()
    {
        var scope = LeadershipScope.ForShard("test-ns", "5");

        scope.ScopeId.ShouldBe("shard/test-ns/5");
        scope.ScopeKind.ShouldBe(LeadershipScopeKinds.Shard);
        scope.Labels["namespace"].ShouldBe("test-ns");
        scope.Labels["shardId"].ShouldBe("5");
    }

    [Fact]
    public void Create_WithCustomKind_CreatesCustomScope()
    {
        var scope = LeadershipScope.Create("custom-scope-id", LeadershipScopeKinds.Custom);

        scope.ScopeId.ShouldBe("custom-scope-id");
        scope.ScopeKind.ShouldBe(LeadershipScopeKinds.Custom);
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

        scope.Labels.Count.ShouldBe(2);
        scope.Labels["key1"].ShouldBe("value1");
        scope.Labels["key2"].ShouldBe("value2");
    }

    [Theory]
    [InlineData("global-control", "global-control")]
    [InlineData("shard/ns/1", "shard/ns/1")]
    [InlineData("custom-scope", "custom-scope")]
    public void TryParse_ValidInput_ReturnsTrue(string input, string expectedScopeId)
    {
        var result = LeadershipScope.TryParse(input, out var scope);

        result.ShouldBeTrue();
        scope.ScopeId.ShouldBe(expectedScopeId);
    }

    [Fact]
    public void TryParse_EmptyString_ReturnsFalse()
    {
        var result = LeadershipScope.TryParse("", out var scope);

        result.ShouldBeFalse();
    }
}
