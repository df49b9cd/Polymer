using System.Diagnostics;

namespace OmniRelay.IntegrationTests.Support;

internal static class OmniRelayCliTestHelper
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    private static readonly string RepositoryRoot = RepositoryPaths.Root;
    private static readonly string CliProjectPath = Path.Combine(RepositoryRoot, "src", "OmniRelay.Cli", "OmniRelay.Cli.csproj");
    private static readonly object BuildLock = new();
    private static bool _cliBuilt;

    public static Task<CliResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken) =>
        RunCliAsync([.. arguments], cancellationToken);

    public static CliBackgroundProcess StartBackground(IEnumerable<string> arguments)
    {
        var psi = CreateProcessStartInfo(arguments);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start OmniRelay CLI process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        return new CliBackgroundProcess(process, stdoutTask, stderrTask);
    }

    private static async Task<CliResult> RunCliAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var psi = CreateProcessStartInfo(arguments);
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start OmniRelay CLI process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKill(process);
            }
        }

        var stdout = (await stdoutTask.ConfigureAwait(false)).TrimEnd();
        var stderr = (await stderrTask.ConfigureAwait(false)).TrimEnd();
        return new CliResult(process.ExitCode, stdout, stderr);
    }

    private static ProcessStartInfo CreateProcessStartInfo(IEnumerable<string> cliArguments)
    {
        EnsureCliBuilt();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = RepositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(CliProjectPath);
        psi.ArgumentList.Add("--configuration");
        psi.ArgumentList.Add(BuildConfiguration);
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--");
        foreach (var argument in cliArguments)
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        return psi;
    }

    private static void EnsureCliBuilt()
    {
        if (_cliBuilt)
        {
            return;
        }

        lock (BuildLock)
        {
            if (_cliBuilt)
            {
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = RepositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add(CliProjectPath);
            psi.ArgumentList.Add("--configuration");
            psi.ArgumentList.Add(BuildConfiguration);

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start OmniRelay CLI build.");
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to build OmniRelay CLI:{Environment.NewLine}{stderr.Result}{Environment.NewLine}{stdout.Result}");
            }

            _cliBuilt = true;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}

internal sealed record CliResult(int ExitCode, string StandardOutput, string StandardError)
{
    public int ExitCode { get; init; } = ExitCode;

    public string StandardOutput { get; init; } = StandardOutput;

    public string StandardError { get; init; } = StandardError;
}

internal sealed class CliBackgroundProcess(Process process, Task<string> stdoutTask, Task<string> stderrTask)
    : IAsyncDisposable
{
    private readonly Process _process = process;
    private readonly Task<string> _stdoutTask = stdoutTask;
    private readonly Task<string> _stderrTask = stderrTask;

    public int ProcessId => _process.Id;

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _process.WaitForExitAsync(cancellationToken);

    public async Task<CliResult> GetResultAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = (await _stdoutTask.ConfigureAwait(false)).TrimEnd();
        var stderr = (await _stderrTask.ConfigureAwait(false)).TrimEnd();
        return new CliResult(_process.ExitCode, stdout, stderr);
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();

        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(_stdoutTask, _stderrTask).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _process.Dispose();
        }
    }
}
