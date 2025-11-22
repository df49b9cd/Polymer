using Hugo;
using Microsoft.Data.Sqlite;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public sealed class SqliteDeterministicStateStoreTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void TryAdd_OnlySucceedsForFirstWriter()
    {
        using var temp = new TempFile();
        var store = new SqliteDeterministicStateStore($"Data Source={temp.Path}");
        var record = new DeterministicRecord("kind", 1, [1, 2], DateTimeOffset.UtcNow);

        Assert.True(store.TryAdd("key", record));
        Assert.False(store.TryAdd("key", record));

        Assert.True(store.TryGet("key", out var fetched));
        Assert.Equal(record.Kind, fetched.Kind);
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            if (!File.Exists(Path))
            {
                return;
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.Delete(Path);
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }
}
