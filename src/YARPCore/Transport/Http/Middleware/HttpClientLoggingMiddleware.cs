using System.Diagnostics;
using Microsoft.Extensions.Logging;
using YARPCore.Core;

namespace YARPCore.Transport.Http.Middleware;

/// <summary>
/// Simple logging middleware that records request and response metadata for outbound HTTP calls.
/// </summary>
public sealed class HttpClientLoggingMiddleware(ILogger<HttpClientLoggingMiddleware> logger) : IHttpClientMiddleware
{
    private readonly ILogger<HttpClientLoggingMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async ValueTask<HttpResponseMessage> InvokeAsync(
        HttpClientMiddlewareContext context,
        CancellationToken cancellationToken,
        HttpClientMiddlewareDelegate next)
    {
        using var scope = RequestLoggingScope.Begin(_logger, context.RequestMeta);

        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return await next(context, cancellationToken).ConfigureAwait(false);
        }

        var request = context.Request;
        var startTimestamp = Stopwatch.GetTimestamp();
        _logger.LogInformation(
            "Sending HTTP outbound request {Method} {Uri} (procedure: {Procedure})",
            request.Method,
            request.RequestUri,
            context.RequestMeta.Procedure ?? string.Empty);

        try
        {
            var response = await next(context, cancellationToken).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

            _logger.LogInformation(
                "Received HTTP outbound response {StatusCode} for {Method} {Uri} in {Elapsed:F2} ms",
                (int)response.StatusCode,
                request.Method,
                request.RequestUri,
                elapsed);

            return response;
        }
        catch (Exception ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

            _logger.LogWarning(
                ex,
                "HTTP outbound request {Method} {Uri} failed after {Elapsed:F2} ms",
                request.Method,
                request.RequestUri,
                elapsed);

            throw;
        }
    }
}
