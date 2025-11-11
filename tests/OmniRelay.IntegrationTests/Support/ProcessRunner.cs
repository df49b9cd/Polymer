using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OmniRelay.IntegrationTests.Support;

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        IDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? RepositoryPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                psi.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = false };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var waitTask = process.WaitForExitAsync(cancellationToken);
        if (timeout is { } limit)
        {
            var completed = await Task.WhenAny(waitTask, Task.Delay(limit, cancellationToken)).ConfigureAwait(false);
            if (completed != waitTask)
            {
                TryKill(process);
                throw new TimeoutException($"Process '{fileName}' exceeded timeout of {limit.TotalSeconds:F1}s.");
            }
        }

        await waitTask.ConfigureAwait(false);
        var stdout = (await stdoutTask.ConfigureAwait(false)).TrimEnd();
        var stderr = (await stderrTask.ConfigureAwait(false)).TrimEnd();
        return new ProcessResult(process.ExitCode, stdout, stderr);
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

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public int ExitCode { get; init; } = ExitCode;

    public string StandardOutput { get; init; } = StandardOutput;

    public string StandardError { get; init; } = StandardError;
}
