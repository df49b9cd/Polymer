using Shouldly;
using Xunit;

namespace OmniRelay.Configuration.UnitTests.Configuration;

public sealed class OmniRelayConfigurationExceptionTests
{
    [Fact]
    public void Ctor_WithMessage_PopulatesMessage()
    {
        var exception = new OmniRelayConfigurationException("bad configuration");
        exception.Message.ShouldBe("bad configuration");
        exception.InnerException.ShouldBeNull();
    }

    [Fact]
    public void Ctor_WithMessageAndInner_PopulatesProperties()
    {
        var inner = new InvalidOperationException("boom");
        var exception = new OmniRelayConfigurationException("bad configuration", inner);

        exception.Message.ShouldBe("bad configuration");
        exception.InnerException.ShouldBe(inner);
    }

    [Fact]
    public void Ctor_Default_UsesTypeName()
    {
        var exception = new OmniRelayConfigurationException();
        exception.Message.ShouldContain(nameof(OmniRelayConfigurationException));
        exception.InnerException.ShouldBeNull();
    }
}
