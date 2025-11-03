using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Polymer.Errors;

namespace Polymer.Transport.Http;

/// <summary>
/// ASP.NET Core exception filter that normalizes thrown exceptions into Polymer error responses.
/// </summary>
public sealed class PolymerExceptionFilter(string transport = "http") : IAsyncExceptionFilter
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
        var polymerException = exception switch
        {
            PolymerException pe => pe,
            _ => PolymerErrors.FromException(exception, _transport)
        };

        var statusCode = HttpStatusMapper.ToStatusCode(polymerException.StatusCode);
        var error = polymerException.Error;
        var transport = polymerException.Transport ?? _transport;

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["message"] = polymerException.Message,
            ["status"] = polymerException.StatusCode.ToString(),
            ["code"] = error.Code,
            ["metadata"] = error.Metadata
        };

        var httpContext = context.HttpContext;
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.Headers[HttpTransportHeaders.Transport] = transport;
        httpContext.Response.Headers[HttpTransportHeaders.Status] = polymerException.StatusCode.ToString();
        httpContext.Response.Headers[HttpTransportHeaders.ErrorMessage] = polymerException.Message;

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

public static class PolymerMvcOptionsExtensions
{
    /// <summary>
    /// Adds <see cref="PolymerExceptionFilter"/> to MVC filters so ASP.NET controllers surface Polymer errors consistently.
    /// </summary>
    public static void AddPolymerExceptionFilter(this MvcOptions options, string transport = "http")
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Filters.Add(new PolymerExceptionFilter(transport));
    }
}
