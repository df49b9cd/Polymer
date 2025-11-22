using Grpc.Core;
using Grpc.Core.Interceptors;
using OmniRelay.Errors;

namespace OmniRelay.Transport.Grpc;

/// <summary>
/// gRPC server interceptor that converts thrown exceptions into OmniRelay-aware <see cref="RpcException"/> instances.
/// </summary>
public sealed class GrpcExceptionAdapterInterceptor : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw AdaptException(exception);
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(requestStream, context).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw AdaptException(exception);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(request, responseStream, context).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw AdaptException(exception);
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(requestStream, responseStream, context).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw AdaptException(exception);
        }
    }

    private static RpcException AdaptException(Exception exception)
    {
        var omnirelayException = exception switch
        {
            OmniRelayException pe => pe,
            _ => OmniRelayErrors.FromException(exception, GrpcTransportConstants.TransportName)
        };

        var status = GrpcStatusMapper.ToStatus(omnirelayException.StatusCode, omnirelayException.Message);
        var trailers = GrpcMetadataAdapter.CreateErrorTrailers(omnirelayException.Error);
        var transport = omnirelayException.Transport ?? GrpcTransportConstants.TransportName;
        if (trailers.GetValue(GrpcTransportConstants.TransportTrailer) is null)
        {
            trailers.Add(GrpcTransportConstants.TransportTrailer, transport);
        }
        return new RpcException(status, trailers, omnirelayException.Message);
    }
}
