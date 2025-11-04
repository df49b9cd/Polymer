using System.Diagnostics;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;
using Polymer.Core;

namespace Polymer.Transport.Grpc;

public sealed class GrpcClientLoggingInterceptor(ILogger<GrpcClientLoggingInterceptor> logger) : Interceptor
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

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Starting gRPC client unary call {Method}", methodName);
        }

        var call = continuation(request, context);
        var responseAsync = LogAsync(call.ResponseAsync, methodName, startTimestamp);

        return new AsyncUnaryCall<TResponse>(
            responseAsync,
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    private async Task<TResponse> LogAsync<TResponse>(Task<TResponse> responseTask, string methodName, long startTimestamp)
    {
        try
        {
            var response = await responseTask.ConfigureAwait(false);
            RecordSuccess(methodName, startTimestamp);
            return response;
        }
        catch (RpcException rpcException)
        {
            RecordFailure(methodName, startTimestamp, rpcException.Status.StatusCode, rpcException.Status.Detail);
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(methodName, startTimestamp, StatusCode.Unknown, ex.Message);
            throw;
        }
    }

    private void RecordSuccess(string methodName, long startTimestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        GrpcTransportMetrics.ClientUnaryDuration.Record(
            elapsed,
            KeyValuePair.Create<string, object?>("rpc.system", "grpc"),
            KeyValuePair.Create<string, object?>("rpc.method", methodName),
            KeyValuePair.Create<string, object?>("rpc.grpc.status_code", StatusCode.OK));

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Completed gRPC client unary call {Method} in {Elapsed:F2} ms",
                methodName,
                elapsed);
        }
    }

    private void RecordFailure(string methodName, long startTimestamp, StatusCode statusCode, string? detail)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        GrpcTransportMetrics.ClientUnaryDuration.Record(
            elapsed,
            KeyValuePair.Create<string, object?>("rpc.system", "grpc"),
            KeyValuePair.Create<string, object?>("rpc.method", methodName),
            KeyValuePair.Create<string, object?>("rpc.grpc.status_code", statusCode));

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "Failed gRPC client unary call {Method} in {Elapsed:F2} ms with status {Status}: {Detail}",
                methodName,
                elapsed,
                statusCode,
                detail);
        }
    }
}

public sealed class GrpcServerLoggingInterceptor(ILogger<GrpcServerLoggingInterceptor> logger) : Interceptor
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

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Handling gRPC server unary call {Method}", methodName);
        }

        try
        {
            var response = await continuation(request, context).ConfigureAwait(false);
            RecordSuccess(methodName, startTimestamp);
            return response;
        }
        catch (RpcException rpcException)
        {
            RecordFailure(methodName, startTimestamp, rpcException.Status.StatusCode, rpcException.Status.Detail);
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(methodName, startTimestamp, StatusCode.Unknown, ex.Message);
            throw;
        }
    }

    private void RecordSuccess(string methodName, long startTimestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        GrpcTransportMetrics.ServerUnaryDuration.Record(
            elapsed,
            KeyValuePair.Create<string, object?>("rpc.system", "grpc"),
            KeyValuePair.Create<string, object?>("rpc.method", methodName),
            KeyValuePair.Create<string, object?>("rpc.grpc.status_code", StatusCode.OK));

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Completed gRPC server unary call {Method} in {Elapsed:F2} ms",
                methodName,
                elapsed);
        }
    }

    private void RecordFailure(string methodName, long startTimestamp, StatusCode statusCode, string? detail)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        GrpcTransportMetrics.ServerUnaryDuration.Record(
            elapsed,
            KeyValuePair.Create<string, object?>("rpc.system", "grpc"),
            KeyValuePair.Create<string, object?>("rpc.method", methodName),
            KeyValuePair.Create<string, object?>("rpc.grpc.status_code", statusCode));

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "Failed gRPC server unary call {Method} in {Elapsed:F2} ms with status {Status}: {Detail}",
                methodName,
                elapsed,
                statusCode,
                detail);
        }
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
            return Enumerable.Empty<KeyValuePair<string, string>>();
        }

        return metadata
            .Where(entry => !entry.IsBinary)
            .Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value ?? string.Empty));
    }
}
