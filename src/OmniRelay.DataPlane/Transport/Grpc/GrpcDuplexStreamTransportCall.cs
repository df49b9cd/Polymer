using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Grpc.Core;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Transport.Grpc;

/// <summary>
/// Client-side transport for gRPC bidirectional streaming calls implementing <see cref="IDuplexStreamCall"/>.
/// Bridges gRPC request/response streams to OmniRelay duplex abstractions and records metrics.
/// </summary>
internal sealed class GrpcDuplexStreamTransportCall : IDuplexStreamCall
{
    private readonly AsyncDuplexStreamingCall<byte[], byte[]> _call;
    private readonly DuplexStreamCall _inner;
    private readonly CancellationTokenSource _cts;
    private ErrGroup? _pumpGroup;
    private readonly KeyValuePair<string, object?>[] _baseTags;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private long _requestCount;
    private long _responseCount;
    private int _metricsRecorded;

    private void CancelCallSilently()
    {
        try
        {
            _call.Dispose();
        }
        catch
        {
            // Best-effort cancellation.
        }
    }

    private GrpcDuplexStreamTransportCall(
        RequestMeta requestMeta,
        AsyncDuplexStreamingCall<byte[], byte[]> call,
        ResponseMeta responseMeta,
        CancellationToken cancellationToken)
    {
        _call = call;
        _inner = DuplexStreamCall.Create(requestMeta, responseMeta);
        _baseTags = GrpcTransportMetrics.CreateBaseTags(requestMeta);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        StartPumps();
    }

    private void StartPumps()
    {
        var group = new ErrGroup(_cts.Token);

        group.Go(async token =>
        {
            await PumpRequestsAsync(token).ConfigureAwait(false);
            return Ok(Unit.Value);
        });

        group.Go(async token =>
        {
            await PumpResponsesAsync(token).ConfigureAwait(false);
            return Ok(Unit.Value);
        });

        _pumpGroup = group;
    }

    /// <summary>
    /// Creates a duplex transport call wrapper from an active gRPC duplex call.
    /// </summary>
    /// <param name="requestMeta">The request metadata.</param>
    /// <param name="call">The active gRPC duplex call.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created duplex call or an error.</returns>
    public static async ValueTask<Result<IDuplexStreamCall>> CreateAsync(
        RequestMeta requestMeta,
        AsyncDuplexStreamingCall<byte[], byte[]> call,
        CancellationToken cancellationToken)
    {
        var creationResult = await Result
            .TryAsync(
                async _ =>
                {
                    var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
                    var responseMeta = GrpcMetadataAdapter.CreateResponseMeta(headers, null);
                    return (IDuplexStreamCall)new GrpcDuplexStreamTransportCall(requestMeta, call, responseMeta, cancellationToken);
                },
                cancellationToken: cancellationToken,
                errorFactory: ex => ex switch
                {
                    RpcException rpcException => OmniRelayErrorAdapter.FromStatus(
                        GrpcStatusMapper.FromStatus(rpcException.Status),
                        string.IsNullOrWhiteSpace(rpcException.Status.Detail)
                            ? rpcException.Status.StatusCode.ToString()
                            : rpcException.Status.Detail,
                        transport: GrpcTransportConstants.TransportName),
                    _ => OmniRelayErrorAdapter.FromStatus(
                        OmniRelayStatusCode.Internal,
                        ex.Message ?? "An error occurred while creating the duplex stream.",
                        transport: GrpcTransportConstants.TransportName,
                        inner: Error.FromException(ex))
                })
            .ConfigureAwait(false);

        return creationResult;
    }

    /// <inheritdoc />
    public RequestMeta RequestMeta => _inner.RequestMeta;

    /// <inheritdoc />
    public ResponseMeta ResponseMeta => _inner.ResponseMeta;

    /// <inheritdoc />
    public DuplexStreamCallContext Context => _inner.Context;

    /// <inheritdoc />
    public ChannelWriter<ReadOnlyMemory<byte>> RequestWriter => _inner.RequestWriter;

    /// <inheritdoc />
    public ChannelReader<ReadOnlyMemory<byte>> RequestReader => _inner.RequestReader;

    /// <inheritdoc />
    public ChannelWriter<ReadOnlyMemory<byte>> ResponseWriter => _inner.ResponseWriter;

    /// <inheritdoc />
    public ChannelReader<ReadOnlyMemory<byte>> ResponseReader => _inner.ResponseReader;

    /// <inheritdoc />
    public ValueTask CompleteRequestsAsync(Error? fault = null, CancellationToken cancellationToken = default) =>
        _inner.CompleteRequestsAsync(fault, cancellationToken);

    /// <inheritdoc />
    public ValueTask CompleteResponsesAsync(Error? fault = null, CancellationToken cancellationToken = default) =>
        _inner.CompleteResponsesAsync(fault, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_pumpGroup is not null)
        {
            var hasPumpResult = false;
            Result<Unit> pumpResult = default;

            try
            {
                pumpResult = await _pumpGroup.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                hasPumpResult = true;
            }
            catch (Exception ex)
            {
                HandlePumpGroupFailure(Error.FromException(ex));
            }
            finally
            {
                _pumpGroup.Dispose();
                _pumpGroup = null;
            }

            if (hasPumpResult && pumpResult.IsFailure && pumpResult.Error is { } pumpError)
            {
                HandlePumpGroupFailure(pumpError);
            }
        }

        _call.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        RecordCompletion(StatusCode.Cancelled);
    }

    private async Task PumpRequestsAsync(CancellationToken cancellationToken)
    {
        var requestStatus = StatusCode.OK;

        var pumpResult = await Result
            .TryAsync(
                async token =>
                {
                    await foreach (var payload in _inner.RequestReader.ReadAllAsync(token).ConfigureAwait(false))
                    {
                        Interlocked.Increment(ref _requestCount);
                        GrpcTransportMetrics.ClientDuplexRequestMessages.Add(1, _baseTags);
                        if (MemoryMarshal.TryGetArray(payload, out var segment) &&
                            segment.Array is { } array &&
                            segment.Offset == 0 &&
                            segment.Count == array.Length)
                        {
                            await _call.RequestStream.WriteAsync(array, token).ConfigureAwait(false);
                        }
                        else
                        {
                            var rented = ArrayPool<byte>.Shared.Rent(payload.Length);
                            try
                            {
                                payload.Span.CopyTo(rented);
                                await _call.RequestStream.WriteAsync(rented, token).ConfigureAwait(false);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(rented);
                            }
                        }
                    }

                    await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
                    return Unit.Value;
                },
                cancellationToken: cancellationToken,
                errorFactory: ex => MapRequestPumpException(ex, ref requestStatus))
            .ConfigureAwait(false);

        if (pumpResult.IsFailure && pumpResult.Error is { } error)
        {
            if (OmniRelayErrorAdapter.ToStatus(error) == OmniRelayStatusCode.Cancelled)
            {
                await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
                CancelCallSilently();
                return;
            }

            await _inner.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
            RecordCompletion(requestStatus);
            CancelCallSilently();
        }
    }

    private async Task PumpResponsesAsync(CancellationToken cancellationToken)
    {
        var responseStatus = StatusCode.OK;

        var pumpResult = await Result
            .TryAsync(
                async token =>
                {
                    await foreach (var payload in _call.ResponseStream.ReadAllAsync(token).ConfigureAwait(false))
                    {
                        Interlocked.Increment(ref _responseCount);
                        GrpcTransportMetrics.ClientDuplexResponseMessages.Add(1, _baseTags);
                        await _inner.ResponseWriter.WriteAsync(payload, token).ConfigureAwait(false);
                    }

                    var trailers = _call.GetTrailers();
                    _inner.SetResponseMeta(GrpcMetadataAdapter.CreateResponseMeta(null, trailers));
                    await _inner.CompleteResponsesAsync(cancellationToken: token).ConfigureAwait(false);
                    RecordCompletion(StatusCode.OK);
                    return Unit.Value;
                },
                cancellationToken: cancellationToken,
                errorFactory: ex => MapResponsePumpException(ex, ref responseStatus))
            .ConfigureAwait(false);

        if (pumpResult.IsFailure && pumpResult.Error is { } error)
        {
            await _inner.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
            RecordCompletion(responseStatus);
            CancelCallSilently();
        }
    }

    private static Error MapRequestPumpException(Exception exception, ref StatusCode completionStatus)
    {
        return exception switch
        {
            RpcException rpcException =>
                MapRpcError(rpcException, ref completionStatus),
            OperationCanceledException canceled =>
                MapCanceled(canceled, ref completionStatus),
            _ => MapInternal(exception, ref completionStatus, "An error occurred while sending request stream.")
        };

        static Error MapRpcError(RpcException rpcException, ref StatusCode completionStatus)
        {
            completionStatus = rpcException.Status.StatusCode;
            return MapRpcException(rpcException);
        }

        static Error MapCanceled(OperationCanceledException canceled, ref StatusCode completionStatus)
        {
            completionStatus = StatusCode.Cancelled;
            return OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Cancelled,
                canceled.Message ?? "The gRPC duplex request pump was cancelled.",
                transport: GrpcTransportConstants.TransportName,
                inner: Error.Canceled().WithCause(canceled));
        }

        static Error MapInternal(Exception exception, ref StatusCode completionStatus, string fallback)
        {
            completionStatus = StatusCode.Unknown;
            return OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Internal,
                exception.Message ?? fallback,
                transport: GrpcTransportConstants.TransportName,
                inner: Error.FromException(exception));
        }
    }

    private static Error MapResponsePumpException(Exception exception, ref StatusCode completionStatus)
    {
        return exception switch
        {
            RpcException rpcException =>
                MapRpcError(rpcException, ref completionStatus),
            OperationCanceledException canceled =>
                MapCanceled(canceled, ref completionStatus),
            _ => MapInternal(exception, ref completionStatus, "An error occurred while reading response stream.")
        };

        static Error MapRpcError(RpcException rpcException, ref StatusCode completionStatus)
        {
            completionStatus = rpcException.Status.StatusCode;
            return MapRpcException(rpcException);
        }

        static Error MapCanceled(OperationCanceledException canceled, ref StatusCode completionStatus)
        {
            completionStatus = StatusCode.Cancelled;
            return OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Cancelled,
                canceled.Message ?? "The gRPC duplex call was cancelled.",
                transport: GrpcTransportConstants.TransportName,
                inner: Error.Canceled().WithCause(canceled));
        }

        static Error MapInternal(Exception exception, ref StatusCode completionStatus, string fallback)
        {
            completionStatus = StatusCode.Unknown;
            return OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Internal,
                exception.Message ?? fallback,
                transport: GrpcTransportConstants.TransportName,
                inner: Error.FromException(exception));
        }
    }

    private static Error MapRpcException(RpcException rpcException)
    {
        var status = GrpcStatusMapper.FromStatus(rpcException.Status);
        var message = string.IsNullOrWhiteSpace(rpcException.Status.Detail)
            ? rpcException.Status.StatusCode.ToString()
            : rpcException.Status.Detail;
        return OmniRelayErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
    }

    private void RecordCompletion(StatusCode statusCode)
    {
        if (Interlocked.Exchange(ref _metricsRecorded, 1) == 1)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        var tags = GrpcTransportMetrics.AppendStatus(_baseTags, statusCode);
        GrpcTransportMetrics.ClientDuplexDuration.Record(elapsed, tags);
        GrpcTransportMetrics.ClientDuplexRequestCount.Record(_requestCount, tags);
        GrpcTransportMetrics.ClientDuplexResponseCount.Record(_responseCount, tags);
    }

    private void HandlePumpGroupFailure(Error pumpError)
    {
        var status = GrpcStatusMapper.ToStatus(
            OmniRelayErrorAdapter.ToStatus(pumpError),
            pumpError.Message ?? "The duplex stream pumps failed.");
        RecordCompletion(status.StatusCode);
    }
}
