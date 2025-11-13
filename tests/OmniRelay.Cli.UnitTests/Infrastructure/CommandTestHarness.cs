using Hugo;

namespace OmniRelay.Cli.UnitTests.Infrastructure;

internal sealed class CommandTestHarness
{
    private readonly RootCommand _rootCommand;
    private readonly ParserConfiguration _parserConfiguration = new();

    public CommandTestHarness(RootCommand rootCommand)
    {
        _rootCommand = rootCommand ?? throw new ArgumentNullException(nameof(rootCommand));
    }

    public async Task<CommandInvocationResult> InvokeAsync(params string[] args)
    {
        using var capture = new ConsoleCapture();
        var parseResult = _rootCommand.Parse(args ?? Array.Empty<string>(), _parserConfiguration);
        var invocationConfiguration = new InvocationConfiguration
        {
            Output = capture.OutWriter,
            Error = capture.ErrorWriter
        };

        var exitCode = await parseResult.InvokeAsync(invocationConfiguration, CancellationToken.None).ConfigureAwait(false);
        return new CommandInvocationResult(exitCode, capture.StandardOutput, capture.StandardError);
    }
}

internal sealed record CommandInvocationResult(int ExitCode, string StdOut, string StdErr);
