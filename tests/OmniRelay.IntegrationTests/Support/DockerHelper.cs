using Xunit;

namespace OmniRelay.IntegrationTests.Support;

internal static class DockerHelper
{
    public static async Task<string> RequireAsync(CancellationToken cancellationToken)
    {
        var dockerPath = ExternalTool.Locate("docker");
        if (dockerPath is null)
        {
            Assert.Skip("Docker CLI not found. Envoy proxy scenarios are skipped.");
        }

        try
        {
            var info = await ProcessRunner.RunAsync(
                dockerPath,
                ["info", "--format", "{{.ServerVersion}}"],
                TimeSpan.FromSeconds(10),
                RepositoryPaths.Root,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (info.ExitCode != 0)
            {
                Assert.Skip($"Docker daemon unavailable: {info.StandardError}");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Assert.Skip($"Docker daemon unavailable: {ex.Message}");
        }

        return dockerPath;
    }
}
