using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;
using Xunit;

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

        exceptionContext.ExceptionHandled.ShouldBeTrue();
        var result = exceptionContext.Result.ShouldBeOfType<ObjectResult>();
        result.StatusCode.ShouldBe(HttpStatusMapper.ToStatusCode(OmniRelayStatusCode.DeadlineExceeded));

        var payload = result.Value.ShouldBeOfType<Dictionary<string, object?>>();
        payload["message"].ShouldBe("deadline");
        payload["status"].ShouldBe(nameof(OmniRelayStatusCode.DeadlineExceeded));
        payload["code"].ShouldBe(OmniRelayErrorAdapter.GetStatusName(OmniRelayStatusCode.DeadlineExceeded));
        var metadata = payload["metadata"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>();
        metadata["omnirelay.transport"].ShouldBe("http");
        metadata.TryGetValue(OmniRelayErrorAdapter.RetryableMetadataKey, out var retryable).ShouldBeTrue();
        retryable.ShouldBe(true);

        httpContext.Response.Headers[HttpTransportHeaders.Transport].ToString().ShouldBe("http");
        httpContext.Response.Headers[HttpTransportHeaders.Status].ToString().ShouldBe(nameof(OmniRelayStatusCode.DeadlineExceeded));
        httpContext.Response.Headers[HttpTransportHeaders.ErrorMessage].ToString().ShouldBe("deadline");
    }
}
