using Xunit;

namespace OmniRelay.IntegrationTests.Support;

internal static class ExternalTool
{
    public static string Require(string toolName, string? skipReason = null)
    {
        var path = Locate(toolName);
        if (path is null)
        {
            Assert.Skip(skipReason ?? $"Tool '{toolName}' was not found on PATH.");
        }

        return path;
    }

    public static string? Locate(string toolName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue))
        {
            return null;
        }

        var segments = pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT")?
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? [".exe", ".cmd", ".bat"])
            : [string.Empty];

        foreach (var segment in segments)
        {
            var candidateBase = Path.Combine(segment, toolName);
            if (OperatingSystem.IsWindows())
            {
                foreach (var extension in extensions)
                {
                    var candidate = candidateBase.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                        ? candidateBase
                        : candidateBase + extension;
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            else if (File.Exists(candidateBase))
            {
                return candidateBase;
            }
        }

        return null;
    }
}
