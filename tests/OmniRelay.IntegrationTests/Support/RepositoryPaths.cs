namespace OmniRelay.IntegrationTests.Support;

internal static class RepositoryPaths
{
    private static readonly Lazy<string> RootLazy = new(ResolveRepositoryRoot, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Root => RootLazy.Value;

    private static string ResolveRepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "OmniRelay.slnx")) ||
                File.Exists(Path.Combine(directory, "OmniRelay.sln")))
            {
                return directory;
            }

            var parent = Directory.GetParent(directory);
            if (parent is null)
            {
                break;
            }
            directory = parent.FullName;
        }

        throw new InvalidOperationException("Failed to locate the OmniRelay repository root from the current test directory.");
    }
}
