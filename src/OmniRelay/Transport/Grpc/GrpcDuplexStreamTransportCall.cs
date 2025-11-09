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
        try
        {
            var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var responseMeta = GrpcMetadataAdapter.CreateResponseMeta(headers, null);
            var transportCall = new GrpcDuplexStreamTransportCall(requestMeta, call, responseMeta, cancellationToken);
            return Ok((IDuplexStreamCall)transportCall);
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            return Err<IDuplexStreamCall>(OmniRelayErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName));
        }
        catch (Exception ex)
        {
            return OmniRelayErrors.ToResult<IDuplexStreamCall>(ex, transport: GrpcTransportConstants.TransportName);
        }
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
    public ValueTask CompleteRequestsAsync(Error? error = null, CancellationToken cancellationToken = default) =>
        _inner.CompleteRequestsAsync(error, cancellationToken);

    /// <inheritdoc />
    public ValueTask CompleteResponsesAsync(Error? error = null, CancellationToken cancellationToken = default) =>
        _inner.CompleteResponsesAsync(error, cancellationToken);

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
        try
        {
            await foreach (var payload in _inner.RequestReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _requestCount);
                GrpcTransportMetrics.ClientDuplexRequestMessages.Add(1, _baseTags);
                await _call.RequestStream.WriteAsync(payload.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (RpcException rpcEx)
        {
            var error = MapRpcException(rpcEx);
            await _inner.CompleteResponsesAsync(error, cancellationToken).ConfigureAwait(false);
            RecordCompletion(rpcEx.Status.StatusCode);
        }
        catch (Exception ex)
        {
            await _inner.CompleteResponsesAsync(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Internal,
                ex.Message ?? "An error occurred while sending request stream.",
                transport: GrpcTransportConstants.TransportName,
                inner: Error.FromException(ex)), cancellationToken).ConfigureAwait(false);
            RecordCompletion(StatusCode.Unknown);
        }
    }

    private async Task PumpResponsesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in _call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _responseCount);
                GrpcTransportMetrics.ClientDuplexResponseMessages.Add(1, _baseTags);
                await _inner.ResponseWriter.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            var trailers = _call.GetTrailers();
            _inner.SetResponseMeta(GrpcMetadataAdapter.CreateResponseMeta(null, trailers));
            await _inner.CompleteResponsesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            RecordCompletion(StatusCode.OK);
        }
        catch (OperationCanceledException)
        {
            var error = OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Cancelled,
                "The gRPC duplex call was cancelled.",
                transport: GrpcTransportConstants.TransportName);
            await _inner.CompleteResponsesAsync(error, cancellationToken).ConfigureAwait(false);
            RecordCompletion(StatusCode.Cancelled);
        }
        catch (RpcException rpcEx)
        {
            var error = MapRpcException(rpcEx);
            await _inner.CompleteResponsesAsync(error, cancellationToken).ConfigureAwait(false);
            RecordCompletion(rpcEx.Status.StatusCode);
        }
        catch (Exception ex)
        {
            await _inner.CompleteResponsesAsync(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Internal,
                ex.Message ?? "An error occurred while reading response stream.",
                transport: GrpcTransportConstants.TransportName,
                inner: Error.FromException(ex)), cancellationToken).ConfigureAwait(false);
            RecordCompletion(StatusCode.Unknown);
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
