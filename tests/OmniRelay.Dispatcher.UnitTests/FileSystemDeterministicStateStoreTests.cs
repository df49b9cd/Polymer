using System;
using System.IO;
using Hugo;
using OmniRelay.Dispatcher;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public sealed class FileSystemDeterministicStateStoreTests
{
    [Fact]
    public void Set_OverwritesExistingRecords()
    {
        using var temp = new TempDirectory();
        var store = new FileSystemDeterministicStateStore(temp.Path);

        var record1 = new DeterministicRecord("kind", 1, [1], DateTimeOffset.UtcNow);
        store.Set("key", record1);

        var record2 = new DeterministicRecord("kind", 2, [2], DateTimeOffset.UtcNow.AddMinutes(1));
        store.Set("key", record2);

        Assert.True(store.TryGet("key", out var fetched));
        Assert.Equal(2, fetched.Version);
    }

    [Fact]
    public void TryAdd_ReturnsFalseWhenFileExists()
    {
        using var temp = new TempDirectory();
        var store = new FileSystemDeterministicStateStore(temp.Path);
        var record = new DeterministicRecord("kind", 1, [1], DateTimeOffset.UtcNow);

        Assert.True(store.TryAdd("key", record));
        Assert.False(store.TryAdd("key", record));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fs-store-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
