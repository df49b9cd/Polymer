using Microsoft.AspNetCore.Http;
using Xunit;
using YARPCore.Errors;
using YARPCore.Transport.Http;

namespace YARPCore.Tests.Transport;

public sealed class HttpStatusMapperTests
{
    [Theory]
    [InlineData(PolymerStatusCode.InvalidArgument, StatusCodes.Status400BadRequest)]
    [InlineData(PolymerStatusCode.DeadlineExceeded, StatusCodes.Status504GatewayTimeout)]
    [InlineData(PolymerStatusCode.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(PolymerStatusCode.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(PolymerStatusCode.Internal, StatusCodes.Status500InternalServerError)]
    [InlineData(PolymerStatusCode.ResourceExhausted, StatusCodes.Status429TooManyRequests)]
    public void ToStatusCode_MapsExpectedValues(PolymerStatusCode statusCode, int expectedHttp)
    {
        Assert.Equal(expectedHttp, HttpStatusMapper.ToStatusCode(statusCode));
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest, PolymerStatusCode.InvalidArgument)]
    [InlineData(StatusCodes.Status408RequestTimeout, PolymerStatusCode.DeadlineExceeded)]
    [InlineData(StatusCodes.Status409Conflict, PolymerStatusCode.Aborted)]
    [InlineData(StatusCodes.Status503ServiceUnavailable, PolymerStatusCode.Unavailable)]
    [InlineData(StatusCodes.Status501NotImplemented, PolymerStatusCode.Unimplemented)]
    public void FromStatusCode_MapsExpectedValues(int httpStatus, PolymerStatusCode expected)
    {
        Assert.Equal(expected, HttpStatusMapper.FromStatusCode(httpStatus));
    }
}
