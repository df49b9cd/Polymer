namespace OmniRelay.Cli.UnitTests.Infrastructure;

public abstract class CliTestBase : IDisposable
{
    protected CliTestBase()
    {
        CliRuntime.Reset();
        BenchmarkRunner.ResetForTests();
    }

    public void Dispose()
    {
        CliRuntime.Reset();
        BenchmarkRunner.ResetForTests();
    }
}
