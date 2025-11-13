using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OmniRelay.Core;

namespace OmniRelay.Transport.Http.Middleware;

/// <summary>
/// Simple logging middleware that records request and response metadata for outbound HTTP calls.
/// </summary>
public sealed partial class HttpClientLoggingMiddleware(ILogger<HttpClientLoggingMiddleware> logger) : IHttpClientMiddleware
{
    private readonly ILogger<HttpClientLoggingMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async ValueTask<HttpResponseMessage> InvokeAsync(
        HttpClientMiddlewareContext context,
        HttpClientMiddlewareHandler nextHandler,
        CancellationToken cancellationToken)
    {
        using var scope = RequestLoggingScope.Begin(_logger, context.RequestMeta);

        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return await nextHandler(context, cancellationToken).ConfigureAwait(false);
        }

        var request = context.Request;
        var startTimestamp = Stopwatch.GetTimestamp();
        HttpClientLoggingLog.RequestStart(
            _logger,
            request.Method.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            context.RequestMeta.Procedure ?? string.Empty);

        try
        {
            var response = await nextHandler(context, cancellationToken).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

            HttpClientLoggingLog.ResponseReceived(
                _logger,
                (int)response.StatusCode,
                request.Method.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                elapsed);

            return response;
        }
        catch (Exception ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

            HttpClientLoggingLog.RequestFailed(
                _logger,
                request.Method.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                elapsed,
                ex);

            throw;
        }
    }

    private static partial class HttpClientLoggingLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Sending HTTP outbound request {Method} {Uri} (procedure: {Procedure})")]
        public static partial void RequestStart(ILogger logger, string method, string uri, string procedure);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Received HTTP outbound response {StatusCode} for {Method} {Uri} in {Elapsed:F2} ms")]
        public static partial void ResponseReceived(ILogger logger, int statusCode, string method, string uri, double elapsed);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "HTTP outbound request {Method} {Uri} failed after {Elapsed:F2} ms")]
        public static partial void RequestFailed(ILogger logger, string method, string uri, double elapsed, Exception exception);
    }
}
