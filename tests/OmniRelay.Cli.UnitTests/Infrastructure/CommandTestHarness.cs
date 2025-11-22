namespace OmniRelay.Cli.UnitTests.Infrastructure;

internal sealed class CommandTestHarness(RootCommand rootCommand)
{
    private readonly RootCommand _rootCommand = rootCommand ?? throw new ArgumentNullException(nameof(rootCommand));
    private readonly ParserConfiguration _parserConfiguration = new();

    public Task<CommandInvocationResult> InvokeAsync(params string[] args) =>
        InvokeAsync(CancellationToken.None, args);

    public async Task<CommandInvocationResult> InvokeAsync(CancellationToken cancellationToken, params string[] args)
    {
        using var capture = new ConsoleCapture();
        var parseResult = _rootCommand.Parse(args ?? Array.Empty<string>(), _parserConfiguration);
        var invocationConfiguration = new InvocationConfiguration
        {
            Output = capture.OutWriter,
            Error = capture.ErrorWriter
        };

        var exitCode = await parseResult.InvokeAsync(invocationConfiguration, cancellationToken).ConfigureAwait(false);
        return new CommandInvocationResult(exitCode, capture.StandardOutput, capture.StandardError);
    }
}

internal sealed record CommandInvocationResult(int ExitCode, string StdOut, string StdErr);
