using System.Diagnostics;
using System.IO;

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

    public static async Task<CliCommandResult> RunAsync(string arguments, CancellationToken cancellationToken)
    {
        if (!File.Exists(CliAssemblyPath))
        {
            throw new FileNotFoundException($"CLI assembly was not found at '{CliAssemblyPath}'. Build OmniRelay.Cli before running feature tests.");
        }

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
}

internal sealed record CliCommandResult(int ExitCode, string Stdout, string Stderr);
