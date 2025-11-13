using System.Threading.Channels;
using Hugo;
using static Hugo.Go;

namespace OmniRelay.Core.Peers;

/// <summary>
/// Async auto-reset signal used by peer choosers to wait for availability notifications.
/// </summary>
internal sealed class PeerAvailabilitySignal(TimeProvider timeProvider) : IDisposable
{
    private readonly Channel<bool> _channel = MakeChannel<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = false,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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

        var waitResult = await WithTimeoutAsync(
            async token =>
            {
                try
                {
                    await _channel.Reader.ReadAsync(token).ConfigureAwait(false);
                    return Ok(true);
                }
                catch (ChannelClosedException)
                {
                    return Ok(true);
                }
            },
            timeout,
            _timeProvider,
            cancellationToken).ConfigureAwait(false);

        if (waitResult.IsFailure)
        {
            if (waitResult.Error?.Code == ErrorCodes.Canceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Timeout or other transient errors should simply let the caller re-evaluate.
            return;
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
    }
}
