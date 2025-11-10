using System;
using System.IO;
using Hugo;
using OmniRelay.Dispatcher;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public sealed class SqliteDeterministicStateStoreTests
{
    [Fact]
    public void TryAdd_OnlySucceedsForFirstWriter()
    {
        using var temp = new TempFile();
        var store = new SqliteDeterministicStateStore($"Data Source={temp.Path}");
        var record = new DeterministicRecord("kind", 1, new byte[] { 1, 2 }, DateTimeOffset.UtcNow);

        Assert.True(store.TryAdd("key", record));
        Assert.False(store.TryAdd("key", record));

        Assert.True(store.TryGet("key", out var fetched));
        Assert.Equal(record.Kind, fetched.Kind);
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
