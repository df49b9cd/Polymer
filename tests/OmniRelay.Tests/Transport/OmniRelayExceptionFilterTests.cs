using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;

namespace OmniRelay.Tests.Transport;

public sealed class OmniRelayExceptionFilterTests
{
    [Fact]
    public async Task OnExceptionAsync_TransformsExceptionIntoOmniRelayPayload()
    {
        var filter = new OmniRelayExceptionFilter();
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var exceptionContext = new ExceptionContext(actionContext, [])
        {
            Exception = new TimeoutException("deadline")
        };

        await filter.OnExceptionAsync(exceptionContext);

        Assert.True(exceptionContext.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(exceptionContext.Result);
        Assert.Equal(HttpStatusMapper.ToStatusCode(OmniRelayStatusCode.DeadlineExceeded), result.StatusCode);

        var payload = Assert.IsType<Dictionary<string, object?>>(result.Value);
        Assert.Equal("deadline", payload["message"]);
        Assert.Equal(OmniRelayStatusCode.DeadlineExceeded.ToString(), payload["status"]);
        Assert.Equal(OmniRelayErrorAdapter.GetStatusName(OmniRelayStatusCode.DeadlineExceeded), payload["code"]);
        var metadata = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(payload["metadata"]);
        Assert.Equal("http", metadata["yarpcore.transport"]);
        Assert.True(metadata.TryGetValue(OmniRelayErrorAdapter.RetryableMetadataKey, out var retryable));
        Assert.Equal(true, retryable);

        Assert.Equal("http", httpContext.Response.Headers[HttpTransportHeaders.Transport]);
        Assert.Equal(OmniRelayStatusCode.DeadlineExceeded.ToString(), httpContext.Response.Headers[HttpTransportHeaders.Status]);
        Assert.Equal("deadline", httpContext.Response.Headers[HttpTransportHeaders.ErrorMessage]);
    }
}
