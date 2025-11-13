namespace OmniRelay.IntegrationTests.Support;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omnirelay-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public static string Path { get; private set; } = string.Empty;

    public static string Resolve(params string[]? segments)
    {
        if (string.IsNullOrEmpty(Path))
        {
            throw new InvalidOperationException("TempDirectory.Path has not been initialized.");
        }

        if (segments is null || segments.Length == 0)
        {
            return Path;
        }

        return segments.Aggregate(Path, System.IO.Path.Combine);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
