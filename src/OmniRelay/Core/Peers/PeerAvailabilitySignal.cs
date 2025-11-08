using System.Threading;
using System.Threading.Channels;

namespace OmniRelay.Core.Peers;

/// <summary>
/// Async auto-reset signal used by peer choosers to wait for availability notifications.
/// </summary>
internal sealed class PeerAvailabilitySignal : IDisposable
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = false,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    public void Signal()
    {
        _channel.Writer.TryWrite(true);
    }

    public async ValueTask WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await _channel.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout elapsed without a signal; swallow to let the caller re-evaluate deadlines.
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
    }
}
