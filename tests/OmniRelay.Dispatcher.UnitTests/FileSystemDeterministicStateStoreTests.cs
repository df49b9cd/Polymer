using System.Linq;
using Hugo;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public sealed class FileSystemDeterministicStateStoreTests
{
    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryAdd_ReturnsFalseWhenFileExists()
    {
        using var temp = new TempDirectory();
        var store = new FileSystemDeterministicStateStore(temp.Path);
        var record = new DeterministicRecord("kind", 1, [1], DateTimeOffset.UtcNow);

        Assert.True(store.TryAdd("key", record));
        Assert.False(store.TryAdd("key", record));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Set_And_Get_Handle_Long_Keys()
    {
        using var temp = new TempDirectory();
        var store = new FileSystemDeterministicStateStore(temp.Path);

        var longKey = new string('k', 2_048);
        var payload = Enumerable.Range(0, 256).Select(static i => (byte)i).ToArray();
        var record = new DeterministicRecord("kind", 3, payload, DateTimeOffset.UtcNow);

        store.Set(longKey, record);

        Assert.True(store.TryGet(longKey, out var fetched));
        Assert.Equal(payload, fetched.Payload.ToArray());
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
