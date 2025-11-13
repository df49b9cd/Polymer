using Microsoft.Extensions.Logging;
using Xunit;

namespace OmniRelay.IntegrationTests.Support;

public abstract class IntegrationTest : IDisposable
{
    protected IntegrationTest(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        Output = output;
        LoggerFactory = TestLoggerFactory.Create(output);
    }

    protected ITestOutputHelper Output { get; }

    protected ILoggerFactory LoggerFactory { get; }

    public void Dispose()
    {
        LoggerFactory.Dispose();
    }
}
