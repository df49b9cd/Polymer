namespace OmniRelay.Cli.UnitTests.Infrastructure;

public abstract class CliTestBase : IDisposable
{
    protected CliTestBase()
    {
        CliRuntime.Reset();
    }

    public void Dispose()
    {
        CliRuntime.Reset();
    }
}
