using Xunit;

namespace OmniRelay.Core.UnitTests;

public class SmokeTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void It_Works()
    {
        Assert.True(true);
    }
}
