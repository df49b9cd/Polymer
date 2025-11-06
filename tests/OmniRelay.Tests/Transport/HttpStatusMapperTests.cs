using Microsoft.AspNetCore.Http;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Tests.Transport;

public sealed class HttpStatusMapperTests
{
    [Theory]
    [InlineData(OmniRelayStatusCode.InvalidArgument, StatusCodes.Status400BadRequest)]
    [InlineData(OmniRelayStatusCode.DeadlineExceeded, StatusCodes.Status504GatewayTimeout)]
    [InlineData(OmniRelayStatusCode.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(OmniRelayStatusCode.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(OmniRelayStatusCode.Internal, StatusCodes.Status500InternalServerError)]
    [InlineData(OmniRelayStatusCode.ResourceExhausted, StatusCodes.Status429TooManyRequests)]
    public void ToStatusCode_MapsExpectedValues(OmniRelayStatusCode statusCode, int expectedHttp) => Assert.Equal(expectedHttp, HttpStatusMapper.ToStatusCode(statusCode));

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest, OmniRelayStatusCode.InvalidArgument)]
    [InlineData(StatusCodes.Status408RequestTimeout, OmniRelayStatusCode.DeadlineExceeded)]
    [InlineData(StatusCodes.Status409Conflict, OmniRelayStatusCode.Aborted)]
    [InlineData(StatusCodes.Status503ServiceUnavailable, OmniRelayStatusCode.Unavailable)]
    [InlineData(StatusCodes.Status501NotImplemented, OmniRelayStatusCode.Unimplemented)]
    public void FromStatusCode_MapsExpectedValues(int httpStatus, OmniRelayStatusCode expected) => Assert.Equal(expected, HttpStatusMapper.FromStatusCode(httpStatus));
}
