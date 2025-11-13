using System.Text;

namespace OmniRelay.Cli.UnitTests.Infrastructure;

internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly StringWriter _stdout = new(new StringBuilder());
    private readonly StringWriter _stderr = new(new StringBuilder());

    public ConsoleCapture()
    {
        _originalOut = Console.Out;
        _originalError = Console.Error;
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public TextWriter OutWriter => _stdout;
    public TextWriter ErrorWriter => _stderr;
    public string StandardOutput => _stdout.ToString();
    public string StandardError => _stderr.ToString();

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        _stdout.Dispose();
        _stderr.Dispose();
    }
}
