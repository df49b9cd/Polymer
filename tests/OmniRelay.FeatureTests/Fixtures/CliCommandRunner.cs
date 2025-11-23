using System.Diagnostics;

namespace OmniRelay.FeatureTests.Fixtures;

internal static class CliCommandRunner
{
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static readonly string BuildConfiguration =
        Directory.GetParent(AppContext.BaseDirectory)?.Parent?.Name ?? "Debug";

    private static readonly string CliAssemblyPath = Path.Combine(
        RepositoryRoot,
        "src",
        "OmniRelay.Cli",
        "bin",
        BuildConfiguration,
        "net10.0",
        "OmniRelay.Cli.dll");

    private static readonly string CliProjectPath = Path.Combine(RepositoryRoot, "src", "OmniRelay.Cli", "OmniRelay.Cli.csproj");
    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    public static async Task<CliCommandResult> RunAsync(string arguments, CancellationToken cancellationToken)
    {
        await EnsureCliBuiltAsync(cancellationToken).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliAssemblyPath}\" {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepositoryRoot
        };

        using var process = new Process { StartInfo = startInfo };

        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdOut = await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        return new CliCommandResult(process.ExitCode, stdOut, stdErr);
    }

    private static async Task EnsureCliBuiltAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(CliAssemblyPath))
        {
            return;
        }

        await BuildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(CliAssemblyPath))
            {
                return;
            }

            var arguments = $"build \"{CliProjectPath}\" -c {BuildConfiguration} --nologo";
            var startInfo = new ProcessStartInfo("dotnet", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = RepositoryRoot
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                throw new InvalidOperationException($"Failed to build OmniRelay.Cli (exit code {process.ExitCode}).{Environment.NewLine}{stdout}{stderr}");
            }
        }
        finally
        {
            BuildLock.Release();
        }
    }
}

internal sealed record CliCommandResult(int ExitCode, string Stdout, string Stderr);
