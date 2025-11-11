using System.Threading.Tasks;
using OmniRelay.Core;
using Xunit;

namespace OmniRelay.Core.UnitTests.Core;

public class AsyncDisposableHelpersTests
{
    private sealed class DummyAsyncDisposable : IAsyncDisposable
    {
        public static bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AsAsyncDisposable_AssignsOutAndReturnsInstance()
    {
        var dummy = new DummyAsyncDisposable();

        await using var disposable = dummy.AsAsyncDisposable(out var captured);

        Assert.Same(dummy, captured);
        Assert.Same(dummy, disposable);

        await disposable.DisposeAsync();
        Assert.True(DummyAsyncDisposable.Disposed);
    }
}
