using System;
using System.IO;
using System.Threading;

namespace OmniRelay.Dispatcher.UnitTests;

internal static class RepositoryRoot
{
    private static readonly Lazy<string> RootLazy = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Path => RootLazy.Value;

    private static string Resolve()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = System.IO.Path.Combine(current, "OmniRelay.slnx");
            if (File.Exists(candidate))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new InvalidOperationException("Failed to locate OmniRelay repository root from test directory.");
    }
}
