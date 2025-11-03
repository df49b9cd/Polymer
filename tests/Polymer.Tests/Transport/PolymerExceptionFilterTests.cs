using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Polymer.Errors;
using Polymer.Transport.Http;
using Xunit;

namespace Polymer.Tests.Transport;

public sealed class PolymerExceptionFilterTests
{
    [Fact]
    public async Task OnExceptionAsync_TransformsExceptionIntoPolymerPayload()
    {
        var filter = new PolymerExceptionFilter();
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var exceptionContext = new ExceptionContext(actionContext, new List<IFilterMetadata>())
        {
            Exception = new TimeoutException("deadline")
        };

        await filter.OnExceptionAsync(exceptionContext);

        Assert.True(exceptionContext.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(exceptionContext.Result);
        Assert.Equal(HttpStatusMapper.ToStatusCode(PolymerStatusCode.DeadlineExceeded), result.StatusCode);

        var payload = Assert.IsType<Dictionary<string, object?>>(result.Value);
        Assert.Equal("deadline", payload["message"]);
        Assert.Equal(PolymerStatusCode.DeadlineExceeded.ToString(), payload["status"]);
        Assert.Equal(PolymerErrorAdapter.GetStatusName(PolymerStatusCode.DeadlineExceeded), payload["code"]);
        var metadata = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(payload["metadata"]);
        Assert.Equal("http", metadata["polymer.transport"]);
        Assert.True(metadata.TryGetValue(PolymerErrorAdapter.RetryableMetadataKey, out var retryable));
        Assert.Equal(true, retryable);

        Assert.Equal("http", httpContext.Response.Headers[HttpTransportHeaders.Transport]);
        Assert.Equal(PolymerStatusCode.DeadlineExceeded.ToString(), httpContext.Response.Headers[HttpTransportHeaders.Status]);
        Assert.Equal("deadline", httpContext.Response.Headers[HttpTransportHeaders.ErrorMessage]);
    }
}
