using System;
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

internal sealed class GrpcDuplexStreamTransportCall : IDuplexStreamCall
{
    private readonly AsyncDuplexStreamingCall<byte[], byte[]> _call;
    private readonly DuplexStreamCall _inner;
    private readonly CancellationTokenSource _cts;
    private readonly Task _requestPump;
    private readonly Task _responsePump;

    private GrpcDuplexStreamTransportCall(
        RequestMeta requestMeta,
        AsyncDuplexStreamingCall<byte[], byte[]> call,
        ResponseMeta responseMeta,
        CancellationToken cancellationToken)
    {
        _call = call;
        _inner = DuplexStreamCall.Create(requestMeta, responseMeta);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _requestPump = PumpRequestsAsync(_cts.Token);
        _responsePump = PumpResponsesAsync(_cts.Token);
    }

    public static async ValueTask<Result<IDuplexStreamCall>> CreateAsync(
        RequestMeta requestMeta,
        AsyncDuplexStreamingCall<byte[], byte[]> call,
        CancellationToken cancellationToken)
    {
        try
        {
            var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
            var responseMeta = GrpcMetadataAdapter.CreateResponseMeta(headers, null, GrpcTransportConstants.TransportName);
            var transportCall = new GrpcDuplexStreamTransportCall(requestMeta, call, responseMeta, cancellationToken);
            return Ok((IDuplexStreamCall)transportCall);
        }
        catch (RpcException rpcEx)
        {
            var status = GrpcStatusMapper.FromStatus(rpcEx.Status);
            var message = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Status.StatusCode.ToString() : rpcEx.Status.Detail;
            return Err<IDuplexStreamCall>(PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName));
        }
        catch (Exception ex)
        {
            return PolymerErrors.ToResult<IDuplexStreamCall>(ex, transport: GrpcTransportConstants.TransportName);
        }
    }

    public RequestMeta RequestMeta => _inner.RequestMeta;

    public ResponseMeta ResponseMeta => _inner.ResponseMeta;

    public ChannelWriter<ReadOnlyMemory<byte>> RequestWriter => _inner.RequestWriter;

    public ChannelReader<ReadOnlyMemory<byte>> RequestReader => _inner.RequestReader;

    public ChannelWriter<ReadOnlyMemory<byte>> ResponseWriter => _inner.ResponseWriter;

    public ChannelReader<ReadOnlyMemory<byte>> ResponseReader => _inner.ResponseReader;

    public ValueTask CompleteRequestsAsync(Error? error = null, CancellationToken cancellationToken = default) =>
        _inner.CompleteRequestsAsync(error, cancellationToken);

    public ValueTask CompleteResponsesAsync(Error? error = null, CancellationToken cancellationToken = default) =>
        _inner.CompleteResponsesAsync(error, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        try
        {
            await Task.WhenAll(_requestPump, _responsePump).ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }
        finally
        {
            _call.Dispose();
            await _inner.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }

    private async Task PumpRequestsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in _inner.RequestReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _call.RequestStream.WriteAsync(payload.ToArray()).ConfigureAwait(false);
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
            await _inner.CompleteResponsesAsync(error).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _inner.CompleteResponsesAsync(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Internal,
                ex.Message ?? "An error occurred while sending request stream.",
                transport: GrpcTransportConstants.TransportName,
                inner: Error.FromException(ex))).ConfigureAwait(false);
        }
    }

    private async Task PumpResponsesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in _call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _inner.ResponseWriter.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            var trailers = _call.GetTrailers();
            _inner.SetResponseMeta(GrpcMetadataAdapter.CreateResponseMeta(null, trailers, GrpcTransportConstants.TransportName));
            await _inner.CompleteResponsesAsync().ConfigureAwait(false);
        }
        catch (RpcException rpcEx)
        {
            var error = MapRpcException(rpcEx);
            await _inner.CompleteResponsesAsync(error).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _inner.CompleteResponsesAsync(PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Internal,
                ex.Message ?? "An error occurred while reading response stream.",
                transport: GrpcTransportConstants.TransportName,
                inner: Error.FromException(ex))).ConfigureAwait(false);
        }
    }

    private static Error MapRpcException(RpcException rpcException)
    {
        var status = GrpcStatusMapper.FromStatus(rpcException.Status);
        var message = string.IsNullOrWhiteSpace(rpcException.Status.Detail)
            ? rpcException.Status.StatusCode.ToString()
            : rpcException.Status.Detail;
        return PolymerErrorAdapter.FromStatus(status, message, transport: GrpcTransportConstants.TransportName);
    }
}
