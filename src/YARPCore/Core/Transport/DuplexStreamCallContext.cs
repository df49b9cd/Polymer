using Hugo;

namespace YARPCore.Core.Transport;

public sealed class DuplexStreamCallContext
{
    private long _requestMessageCount;
    private long _responseMessageCount;
    private int _requestCompletionStatus;
    private int _responseCompletionStatus;
    private Error? _requestCompletionError;
    private Error? _responseCompletionError;
    private long _requestCompletedAtUtcTicks;
    private long _responseCompletedAtUtcTicks;

    public long RequestMessageCount => Interlocked.Read(ref _requestMessageCount);

    public long ResponseMessageCount => Interlocked.Read(ref _responseMessageCount);

    public StreamCompletionStatus RequestCompletionStatus => (StreamCompletionStatus)Volatile.Read(ref _requestCompletionStatus);

    public StreamCompletionStatus ResponseCompletionStatus => (StreamCompletionStatus)Volatile.Read(ref _responseCompletionStatus);

    public Error? RequestCompletionError => Volatile.Read(ref _requestCompletionError);

    public Error? ResponseCompletionError => Volatile.Read(ref _responseCompletionError);

    public DateTimeOffset? RequestCompletedAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _requestCompletedAtUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public DateTimeOffset? ResponseCompletedAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _responseCompletedAtUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    internal void IncrementRequestMessageCount()
    {
        Interlocked.Increment(ref _requestMessageCount);
    }

    internal void IncrementResponseMessageCount()
    {
        Interlocked.Increment(ref _responseMessageCount);
    }

    internal bool TrySetRequestCompletion(StreamCompletionStatus status, Error? error = null)
    {
        var previous = (StreamCompletionStatus)Interlocked.CompareExchange(
            ref _requestCompletionStatus,
            (int)status,
            (int)StreamCompletionStatus.None);

        if (previous != StreamCompletionStatus.None)
        {
            return false;
        }

        Volatile.Write(ref _requestCompletionError, error);
        var timestamp = DateTimeOffset.UtcNow.UtcTicks;
        Interlocked.CompareExchange(ref _requestCompletedAtUtcTicks, timestamp, 0);
        return true;
    }

    internal bool TrySetResponseCompletion(StreamCompletionStatus status, Error? error = null)
    {
        var previous = (StreamCompletionStatus)Interlocked.CompareExchange(
            ref _responseCompletionStatus,
            (int)status,
            (int)StreamCompletionStatus.None);

        if (previous != StreamCompletionStatus.None)
        {
            return false;
        }

        Volatile.Write(ref _responseCompletionError, error);
        var timestamp = DateTimeOffset.UtcNow.UtcTicks;
        Interlocked.CompareExchange(ref _responseCompletedAtUtcTicks, timestamp, 0);
        return true;
    }
}
