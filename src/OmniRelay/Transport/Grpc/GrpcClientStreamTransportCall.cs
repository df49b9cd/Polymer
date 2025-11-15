using System.Diagnostics;
using System.Threading.Channels;
using Grpc.Core;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Transport.Grpc;

/// <summary>
/// Client-side transport for gRPC client-streaming calls implementing <see cref="IClientStreamTransportCall"/>.
/// Manages request writes, observes the single response, and records metrics.
/// </summary>
internal sealed class GrpcClientStreamTransportCall : IClientStreamTransportCall
{
    private readonly AsyncClientStreamingCall<byte[], byte[]> _call;
    private readonly WriteOptions? _writeOptions;
    private readonly KeyValuePair<string, object?>[] _baseTags;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private long _requestCount;
    private int _metricsRecorded;
    private readonly TaskCompletionSource<Result<Response<ReadOnlyMemory<byte>>>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<byte[]> _pendingWrites;
    private readonly Task _writePump;
    private Error? _terminalError;
    private int _completionRequested;
    private int _completed;
    private bool _disposed;

    /// <summary>
    /// Creates a client-stream transport call bound to an active gRPC call.
    /// </summary>
    /// <param name="requestMeta">The request metadata.</param>
    /// <param name="call">The active gRPC client-streaming call.</param>
    /// <param name="writeOptions">Optional write options applied per message.</param>
    public GrpcClientStreamTransportCall(
        RequestMeta requestMeta,
        AsyncClientStreamingCall<byte[], byte[]> call,
        WriteOptions? writeOptions)
    {
        RequestMeta = requestMeta ?? throw new ArgumentNullException(nameof(requestMeta));
        _call = call ?? throw new ArgumentNullException(nameof(call));
        _writeOptions = writeOptions;
        ResponseMeta = new ResponseMeta();
        _baseTags = GrpcTransportMetrics.CreateBaseTags(requestMeta);
        _pendingWrites = MakeChannel<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _writePump = RunWritePumpAsync();
        _ = ObserveResponseAsync();
    }

    /// <inheritdoc />
    public RequestMeta RequestMeta { get; }

    /// <inheritdoc />
    public ResponseMeta ResponseMeta { get; private set; }

    /// <summary>
    /// Gets the ValueTask that completes with the unary response of the client-streaming call.
    /// </summary>
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> Response =>
        new(_completion.Task);

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(GrpcClientStreamTransportCall));

        cancellationToken.ThrowIfCancellationRequested();

        var buffer = payload.ToArray();

        try
        {
            await _pendingWrites.Writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            if (_terminalError is Error error)
            {
                throw OmniRelayErrors.FromError(error, GrpcTransportConstants.TransportName);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _completionRequested, 1) == 1)
        {
            await _writePump.ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _pendingWrites.Writer.TryComplete();
        await _writePump.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _pendingWrites.Writer.TryComplete();

        try
        {
            await _writePump.ConfigureAwait(false);
        }
        finally
        {
            _call.Dispose();
            RecordCompletion(StatusCode.Cancelled);
        }
    }

    private async Task ObserveResponseAsync()
    {
        StatusCode completionStatus = StatusCode.OK;

        var responseResult = (await Result
                .TryAsync(
                    async _ =>
                    {
                        var headers = await _call.ResponseHeadersAsync.ConfigureAwait(false);
                        var payload = await _call.ResponseAsync.ConfigureAwait(false);
                        var trailers = _call.GetTrailers();
                        return (headers, payload, trailers);
                    },
                    cancellationToken: CancellationToken.None,
                    errorFactory: ex =>
                    {
                        if (ex is RpcException rpcException)
                        {
                            completionStatus = rpcException.Status.StatusCode;
                            return MapRpcException(rpcException);
                        }

                        completionStatus = StatusCode.Unknown;
                        return MapInternalError(
                            ex,
                            "An error occurred while reading the client stream response.");
                    })
                .ConfigureAwait(false))
            .Map(tuple =>
            {
                var (headers, payload, trailers) = tuple;
                var meta = GrpcMetadataAdapter.CreateResponseMeta(headers, trailers);
                return Response<ReadOnlyMemory<byte>>.Create(payload, meta);
            });

        responseResult.Tap(response =>
        {
            ResponseMeta = response.Meta;
            _completion.TrySetResult(Ok(response));
            RecordCompletion(StatusCode.OK);
            _pendingWrites.Writer.TryComplete();
        });

        if (responseResult.IsFailure && responseResult.Error is { } error)
        {
            FailPipeline(error, completionStatus);
        }
    }

    private async Task RunWritePumpAsync()
    {
        StatusCode failureStatus = StatusCode.Unknown;

        var pumpResult = await Result
            .TryAsync(
                async _ =>
                {
                    await foreach (var payload in _pendingWrites.Reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        if (_writeOptions is not null)
                        {
                            _call.RequestStream.WriteOptions = _writeOptions;
                        }

                        await _call.RequestStream.WriteAsync(payload).ConfigureAwait(false);
                        Interlocked.Increment(ref _requestCount);
                        GrpcTransportMetrics.ClientClientStreamRequestMessages.Add(1, _baseTags);
                    }

                    if (Interlocked.Exchange(ref _completed, 1) == 0)
                    {
                        await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
                    }

                    return Unit.Value;
                },
                cancellationToken: CancellationToken.None,
                errorFactory: ex =>
                {
                    if (ex is RpcException rpcException)
                    {
                        failureStatus = rpcException.Status.StatusCode;
                        return MapRpcException(rpcException);
                    }

                    failureStatus = StatusCode.Unknown;
                    return MapInternalError(ex, "An error occurred while writing to the client stream.");
                })
            .ConfigureAwait(false);

        if (pumpResult.IsFailure && pumpResult.Error is { } error)
        {
            FailPipeline(error, failureStatus);
        }
    }

    private void FailPipeline(Error error, StatusCode statusCode)
    {
        _terminalError = error;
        _completion.TrySetResult(Err<Response<ReadOnlyMemory<byte>>>(error));
        RecordCompletion(statusCode);
        var exception = OmniRelayErrors.FromError(error, GrpcTransportConstants.TransportName);
        _pendingWrites.Writer.TryComplete(exception);
    }

    private static Error MapRpcException(RpcException rpcException)
    {
        var status = GrpcStatusMapper.FromStatus(rpcException.Status);
        var message = string.IsNullOrWhiteSpace(rpcException.Status.Detail)
            ? rpcException.Status.StatusCode.ToString()
            : rpcException.Status.Detail;
        return OmniRelayErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
    }

    private static Error MapInternalError(Exception exception, string fallbackMessage) =>
        OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.Internal,
            exception.Message ?? fallbackMessage,
            transport: GrpcTransportConstants.TransportName,
            inner: Error.FromException(exception));

    private void RecordCompletion(StatusCode statusCode)
    {
        if (Interlocked.Exchange(ref _metricsRecorded, 1) == 1)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        var tags = GrpcTransportMetrics.AppendStatus(_baseTags, statusCode);
        GrpcTransportMetrics.ClientClientStreamDuration.Record(elapsed, tags);
        GrpcTransportMetrics.ClientClientStreamRequestCount.Record(_requestCount, tags);
    }
}
