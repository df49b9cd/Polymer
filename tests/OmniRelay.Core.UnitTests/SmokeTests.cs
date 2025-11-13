using Xunit;

namespace OmniRelay.Core.UnitTests;

public class SmokeTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void It_Works()
    {
        var sanity = true;
        sanity.ShouldBeTrue();
    }
}
