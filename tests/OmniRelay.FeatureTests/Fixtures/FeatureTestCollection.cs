using Xunit;

namespace OmniRelay.FeatureTests.Fixtures;

[CollectionDefinition(nameof(FeatureTestCollection))]
public sealed class FeatureTestCollection : ICollectionFixture<FeatureTestApplication>
{
}
