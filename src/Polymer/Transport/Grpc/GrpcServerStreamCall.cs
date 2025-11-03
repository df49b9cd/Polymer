using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using Hugo;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Transport.Grpc;

public sealed class GrpcServerStreamCall : IStreamCall
{
    private readonly Channel<ReadOnlyMemory<byte>> _responses;
    private readonly Channel<ReadOnlyMemory<byte>> _requests;
    private readonly StreamCallContext _context;
    private readonly KeyValuePair<string, object?>[] _baseTags;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private bool _completed;
    private long _responseCount;
    private int _metricsRecorded;

    private GrpcServerStreamCall(RequestMeta requestMeta, ResponseMeta responseMeta)
    {
        RequestMeta = requestMeta ?? throw new ArgumentNullException(nameof(requestMeta));
        ResponseMeta = responseMeta ?? new ResponseMeta();
        _context = new StreamCallContext(StreamDirection.Server);
        _baseTags = GrpcTransportMetrics.CreateBaseTags(requestMeta);

        _responses = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _requests = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        _requests.Writer.TryComplete();
    }

    public static GrpcServerStreamCall Create(RequestMeta requestMeta, ResponseMeta? responseMeta = null) =>
        new(requestMeta, responseMeta ?? new ResponseMeta());

    public StreamDirection Direction => StreamDirection.Server;

    public RequestMeta RequestMeta { get; }

    public ResponseMeta ResponseMeta { get; private set; }

    public StreamCallContext Context => _context;

    public ChannelWriter<ReadOnlyMemory<byte>> Requests => _requests.Writer;

    public ChannelReader<ReadOnlyMemory<byte>> Responses => _responses.Reader;

    public void SetResponseMeta(ResponseMeta meta)
    {
        ResponseMeta = meta ?? new ResponseMeta();
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        await _responses.Writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _responseCount);
        _context.IncrementMessageCount();
        GrpcTransportMetrics.ServerServerStreamResponseMessages.Add(1, _baseTags);
    }

    public ValueTask CompleteAsync(Error? error = null, CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            return ValueTask.CompletedTask;
        }

        _completed = true;

        var completionStatus = ResolveCompletionStatus(error);

        if (error is null)
        {
            _responses.Writer.TryComplete();
            RecordCompletion(StatusCode.OK);
        }
        else
        {
            var exception = PolymerErrors.FromError(error, GrpcTransportConstants.TransportName);
            _responses.Writer.TryComplete(exception);
            var status = GrpcStatusMapper.ToStatus(PolymerErrorAdapter.ToStatus(error), exception.Message);
            RecordCompletion(status.StatusCode);
        }

        _context.TrySetCompletion(completionStatus, error);

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _responses.Writer.TryComplete();
        _requests.Writer.TryComplete();
        RecordCompletion(StatusCode.Cancelled);
        _context.TrySetCompletion(StreamCompletionStatus.Cancelled);
        return ValueTask.CompletedTask;
    }

    private static StreamCompletionStatus ResolveCompletionStatus(Error? error)
    {
        if (error is null)
        {
            return StreamCompletionStatus.Succeeded;
        }

        return PolymerErrorAdapter.ToStatus(error) switch
        {
            PolymerStatusCode.Cancelled => StreamCompletionStatus.Cancelled,
            _ => StreamCompletionStatus.Faulted
        };
    }

    private void RecordCompletion(StatusCode statusCode)
    {
        if (Interlocked.Exchange(ref _metricsRecorded, 1) == 1)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        var tags = GrpcTransportMetrics.AppendStatus(_baseTags, statusCode);
        GrpcTransportMetrics.ServerServerStreamDuration.Record(elapsed, tags);
        GrpcTransportMetrics.ServerServerStreamResponseCount.Record(_responseCount, tags);
    }
}
