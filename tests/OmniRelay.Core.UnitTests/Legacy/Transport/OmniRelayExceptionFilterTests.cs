using AwesomeAssertions;
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
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask OnExceptionAsync_TransformsExceptionIntoOmniRelayPayload()
    {
        var filter = new OmniRelayExceptionFilter();
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var exceptionContext = new ExceptionContext(actionContext, [])
        {
            Exception = new TimeoutException("deadline")
        };

        await filter.OnExceptionAsync(exceptionContext);

        exceptionContext.ExceptionHandled.Should().BeTrue();
        var result = exceptionContext.Result.Should().BeOfType<ObjectResult>().Which;
        result.StatusCode.Should().Be(HttpStatusMapper.ToStatusCode(OmniRelayStatusCode.DeadlineExceeded));

        var payload = result.Value.Should().BeOfType<Dictionary<string, object?>>().Which;
        payload["message"].Should().Be("deadline");
        payload["status"].Should().Be(nameof(OmniRelayStatusCode.DeadlineExceeded));
        payload["code"].Should().Be(OmniRelayErrorAdapter.GetStatusName(OmniRelayStatusCode.DeadlineExceeded));
        var metadata = payload["metadata"].Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Which;
        metadata["omnirelay.transport"].Should().Be("http");
        metadata.TryGetValue(OmniRelayErrorAdapter.RetryableMetadataKey, out var retryable).Should().BeTrue();
        retryable.Should().Be(true);

        httpContext.Response.Headers[HttpTransportHeaders.Transport].ToString().Should().Be("http");
        httpContext.Response.Headers[HttpTransportHeaders.Status].ToString().Should().Be(nameof(OmniRelayStatusCode.DeadlineExceeded));
        httpContext.Response.Headers[HttpTransportHeaders.ErrorMessage].ToString().Should().Be("deadline");
    }
}
