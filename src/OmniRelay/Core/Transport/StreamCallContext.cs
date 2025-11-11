using Hugo;

namespace OmniRelay.Core.Transport;

/// <summary>
/// Tracks message counts and completion information for a server-streaming call.
/// </summary>
public sealed class StreamCallContext(StreamDirection direction)
{
    private long _messageCount;
    private int _completionStatus;
    private Error? _completionError;
    private long _completedAtUtcTicks;

    /// <summary>Gets the stream direction.</summary>
    public StreamDirection Direction { get; } = direction;

    /// <summary>Gets the number of messages written to the stream.</summary>
    public long MessageCount => Interlocked.Read(ref _messageCount);

    /// <summary>Gets the terminal completion status.</summary>
    public StreamCompletionStatus CompletionStatus => (StreamCompletionStatus)Volatile.Read(ref _completionStatus);

    /// <summary>Gets the completion error if the stream faulted.</summary>
    public Error? CompletionError => Volatile.Read(ref _completionError);

    /// <summary>Gets when the stream completed.</summary>
    public DateTimeOffset? CompletedAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _completedAtUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    internal void IncrementMessageCount() => Interlocked.Increment(ref _messageCount);

    internal bool TrySetCompletion(StreamCompletionStatus status, Error? error = null)
    {
        var previous = (StreamCompletionStatus)Interlocked.CompareExchange(
            ref _completionStatus,
            (int)status,
            (int)StreamCompletionStatus.None);

        if (previous != StreamCompletionStatus.None)
        {
            return false;
        }

        Volatile.Write(ref _completionError, error);
        var timestamp = DateTimeOffset.UtcNow.UtcTicks;
        Interlocked.CompareExchange(ref _completedAtUtcTicks, timestamp, 0);
        return true;
    }
}
