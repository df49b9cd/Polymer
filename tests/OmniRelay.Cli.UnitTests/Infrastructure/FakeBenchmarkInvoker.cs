using System.Collections.Concurrent;
using OmniRelay.Core;

namespace OmniRelay.Cli.UnitTests.Infrastructure;

internal sealed class FakeBenchmarkInvoker(TimeSpan? delay = null, bool shouldFail = false, string? errorMessage = null)
    : BenchmarkRunner.IRequestInvoker
{
    private readonly TimeSpan _delay = delay ?? TimeSpan.Zero;
    private readonly bool _shouldFail = shouldFail;
    private readonly string _errorMessage = errorMessage ?? "simulated-error";
    private readonly ConcurrentQueue<DateTimeOffset> _timestamps = new();
    private int _inFlight;
    private int _maxConcurrency;
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);
    public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);
    public IReadOnlyList<DateTimeOffset> CallTimestamps => _timestamps.ToArray();

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<BenchmarkRunner.RequestCallResult> InvokeAsync(Request<ReadOnlyMemory<byte>> request, CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref _inFlight);
        UpdateMaxConcurrency(current);
        _timestamps.Enqueue(DateTimeOffset.UtcNow);
        Interlocked.Increment(ref _callCount);

        try
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }

        return _shouldFail
            ? BenchmarkRunner.RequestCallResult.FromFailure(_errorMessage)
            : BenchmarkRunner.RequestCallResult.FromSuccess();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void UpdateMaxConcurrency(int candidate)
    {
        while (true)
        {
            var observed = _maxConcurrency;
            if (candidate <= observed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _maxConcurrency, candidate, observed) == observed)
            {
                return;
            }
        }
    }
}
