using System.Diagnostics;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;
using OmniRelay.Core;

namespace OmniRelay.Transport.Grpc;

public sealed partial class GrpcClientLoggingInterceptor(ILogger<GrpcClientLoggingInterceptor> logger) : Interceptor
{
    private readonly ILogger<GrpcClientLoggingInterceptor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var methodName = context.Method.FullName;
        var startTimestamp = Stopwatch.GetTimestamp();

        var requestMeta = GrpcLoggingScopeHelper.CreateClientRequestMeta(context);
        using var scope = RequestLoggingScope.Begin(_logger, requestMeta);
        var baseTags = GrpcTransportMetrics.CreateBaseTags(requestMeta);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            ClientLog.ClientCallStart(_logger, methodName);
        }

        var call = continuation(request, context);
        var responseAsync = LogAsync(call.ResponseAsync, methodName, startTimestamp, baseTags);

        return new AsyncUnaryCall<TResponse>(
            responseAsync,
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    private async Task<TResponse> LogAsync<TResponse>(Task<TResponse> responseTask, string methodName, long startTimestamp, KeyValuePair<string, object?>[] baseTags)
    {
        try
        {
            var response = await responseTask.ConfigureAwait(false);
            RecordSuccess(methodName, startTimestamp, baseTags);
            return response;
        }
        catch (RpcException rpcException)
        {
            RecordFailure(methodName, startTimestamp, baseTags, rpcException.Status.StatusCode, rpcException.Status.Detail);
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(methodName, startTimestamp, baseTags, StatusCode.Unknown, ex.Message);
            throw;
        }
    }

    private void RecordSuccess(string methodName, long startTimestamp, KeyValuePair<string, object?>[] baseTags)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var tags = GrpcTransportMetrics.AppendStatus(baseTags, StatusCode.OK);
        GrpcTransportMetrics.ClientUnaryDuration.Record(elapsed, tags);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            ClientLog.ClientCallSuccess(_logger, methodName, elapsed);
        }
    }

    private void RecordFailure(string methodName, long startTimestamp, KeyValuePair<string, object?>[] baseTags, StatusCode statusCode, string? detail)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var tags = GrpcTransportMetrics.AppendStatus(baseTags, statusCode);
        GrpcTransportMetrics.ClientUnaryDuration.Record(elapsed, tags);

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            ClientLog.ClientCallFailure(_logger, methodName, elapsed, statusCode, detail);
        }
    }

    private static partial class ClientLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting gRPC client unary call {Method}")]
        public static partial void ClientCallStart(ILogger logger, string method);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Completed gRPC client unary call {Method} in {Elapsed:F2} ms")]
        public static partial void ClientCallSuccess(ILogger logger, string method, double elapsed);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Failed gRPC client unary call {Method} in {Elapsed:F2} ms with status {Status}: {Detail}")]
        public static partial void ClientCallFailure(ILogger logger, string method, double elapsed, StatusCode status, string? detail);
    }
}

public sealed partial class GrpcServerLoggingInterceptor(ILogger<GrpcServerLoggingInterceptor> logger) : Interceptor
{
    private readonly ILogger<GrpcServerLoggingInterceptor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var methodName = context.Method;
        var startTimestamp = Stopwatch.GetTimestamp();

        var requestMeta = GrpcLoggingScopeHelper.CreateServerRequestMeta(context);
        using var scope = RequestLoggingScope.Begin(_logger, requestMeta);
        var baseTags = GrpcTransportMetrics.CreateBaseTags(requestMeta);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            ServerLog.ServerCallStart(_logger, methodName);
        }

        try
        {
            var response = await continuation(request, context).ConfigureAwait(false);
            RecordSuccess(methodName, startTimestamp, baseTags);
            return response;
        }
        catch (RpcException rpcException)
        {
            RecordFailure(methodName, startTimestamp, baseTags, rpcException.Status.StatusCode, rpcException.Status.Detail);
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(methodName, startTimestamp, baseTags, StatusCode.Unknown, ex.Message);
            throw;
        }
    }

    private void RecordSuccess(string methodName, long startTimestamp, KeyValuePair<string, object?>[] baseTags)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var tags = GrpcTransportMetrics.AppendStatus(baseTags, StatusCode.OK);
        GrpcTransportMetrics.ServerUnaryDuration.Record(elapsed, tags);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            ServerLog.ServerCallSuccess(_logger, methodName, elapsed);
        }
    }

    private void RecordFailure(string methodName, long startTimestamp, KeyValuePair<string, object?>[] baseTags, StatusCode statusCode, string? detail)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var tags = GrpcTransportMetrics.AppendStatus(baseTags, statusCode);
        GrpcTransportMetrics.ServerUnaryDuration.Record(elapsed, tags);

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            ServerLog.ServerCallFailure(_logger, methodName, elapsed, statusCode, detail);
        }
    }

    private static partial class ServerLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Handling gRPC server unary call {Method}")]
        public static partial void ServerCallStart(ILogger logger, string method);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Completed gRPC server unary call {Method} in {Elapsed:F2} ms")]
        public static partial void ServerCallSuccess(ILogger logger, string method, double elapsed);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Failed gRPC server unary call {Method} in {Elapsed:F2} ms with status {Status}: {Detail}")]
        public static partial void ServerCallFailure(ILogger logger, string method, double elapsed, StatusCode status, string? detail);
    }
}

internal static class GrpcLoggingScopeHelper
{
    internal static RequestMeta CreateClientRequestMeta<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var (service, procedure) = ParseMethodName(context.Method.FullName);
        var headers = ExtractHeaders(context.Options.Headers);
        return new RequestMeta(
            service: service,
            procedure: procedure,
            transport: GrpcTransportConstants.TransportName,
            headers: headers);
    }

    internal static RequestMeta CreateServerRequestMeta(ServerCallContext context)
    {
        var (service, procedure) = ParseMethodName(context.Method);
        var headers = ExtractHeaders(context.RequestHeaders);
        var httpContext = context.GetHttpContext();
        if (httpContext is not null && !string.IsNullOrWhiteSpace(httpContext.Request.Protocol))
        {
            headers = headers.Append(new KeyValuePair<string, string>("rpc.protocol", httpContext.Request.Protocol));
        }

        if (!string.IsNullOrWhiteSpace(context.Peer))
        {
            headers = headers.Append(new KeyValuePair<string, string>("rpc.peer", context.Peer));
        }

        return new RequestMeta(
            service: service,
            procedure: procedure,
            transport: GrpcTransportConstants.TransportName,
            headers: headers);
    }

    private static (string Service, string Procedure) ParseMethodName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return ("unknown", string.Empty);
        }

        var trimmed = fullName[0] == '/' ? fullName[1..] : fullName;
        var separatorIndex = trimmed.IndexOf('/');
        if (separatorIndex <= 0)
        {
            return (trimmed, trimmed);
        }

        var service = trimmed[..separatorIndex];
        var method = trimmed[(separatorIndex + 1)..];
        return (service, method);
    }

    private static IEnumerable<KeyValuePair<string, string>> ExtractHeaders(Metadata? metadata)
    {
        if (metadata is null)
        {
            return [];
        }

        return metadata
            .Where(entry => !entry.IsBinary)
            .Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value ?? string.Empty));
    }
}
