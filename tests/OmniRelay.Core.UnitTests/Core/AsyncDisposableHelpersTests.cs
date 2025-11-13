using Xunit;

namespace OmniRelay.Core.UnitTests.Core;

public class AsyncDisposableHelpersTests
{
    private sealed class DummyAsyncDisposable : IAsyncDisposable
    {
        public static bool Disposed { get; private set; }

        public static void Reset() => Disposed = false;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AsAsyncDisposable_AssignsOutAndReturnsInstance()
    {
        DummyAsyncDisposable.Reset();
        var dummy = new DummyAsyncDisposable();

        await using var disposable = dummy.AsAsyncDisposable(out var captured);

        captured.ShouldBeSameAs(dummy);
        disposable.ShouldBeSameAs(dummy);

        await disposable.DisposeAsync();
        DummyAsyncDisposable.Disposed.ShouldBeTrue();
    }
}
