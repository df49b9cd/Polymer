using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using OmniRelay.Errors;

namespace OmniRelay.Transport.Http;

/// <summary>
/// ASP.NET Core exception filter that normalizes thrown exceptions into OmniRelay error responses.
/// </summary>
public sealed class OmniRelayExceptionFilter(string transport = "http") : IAsyncExceptionFilter
{
    private readonly string _transport = string.IsNullOrWhiteSpace(transport) ? "http" : transport;

    public Task OnExceptionAsync(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ExceptionHandled)
        {
            return Task.CompletedTask;
        }

        var exception = context.Exception;
        var omniRelayException = exception switch
        {
            OmniRelayException pe => pe,
            _ => OmniRelayErrors.FromException(exception, _transport)
        };

        var statusCode = HttpStatusMapper.ToStatusCode(omniRelayException.StatusCode);
        var error = omniRelayException.Error;
        var transport = omniRelayException.Transport ?? _transport;

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["message"] = omniRelayException.Message,
            ["status"] = omniRelayException.StatusCode.ToString(),
            ["code"] = error.Code,
            ["metadata"] = error.Metadata
        };

        var httpContext = context.HttpContext;
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.Headers[HttpTransportHeaders.Transport] = transport;
        httpContext.Response.Headers[HttpTransportHeaders.Status] = omniRelayException.StatusCode.ToString();
        httpContext.Response.Headers[HttpTransportHeaders.ErrorMessage] = omniRelayException.Message;

        if (!string.IsNullOrEmpty(error.Code))
        {
            httpContext.Response.Headers[HttpTransportHeaders.ErrorCode] = error.Code;
        }

        context.Result = new ObjectResult(payload)
        {
            StatusCode = statusCode
        };

        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }
}

public static class OmniRelayMvcOptionsExtensions
{
    /// <summary>
    /// Adds <see cref="OmniRelayExceptionFilter"/> to MVC filters so ASP.NET controllers surface OmniRelay errors consistently.
    /// </summary>
    public static void AddOmniRelayExceptionFilter(this MvcOptions options, string transport = "http")
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Filters.Add(new OmniRelayExceptionFilter(transport));
    }
}
