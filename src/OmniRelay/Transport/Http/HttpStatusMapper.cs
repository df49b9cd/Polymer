using Microsoft.AspNetCore.Http;
using OmniRelay.Errors;

namespace OmniRelay.Transport.Http;

internal static class HttpStatusMapper
{
    public static int ToStatusCode(OmniRelayStatusCode status) => status switch
    {
        OmniRelayStatusCode.InvalidArgument => StatusCodes.Status400BadRequest,
        OmniRelayStatusCode.NotFound => StatusCodes.Status404NotFound,
        OmniRelayStatusCode.AlreadyExists => StatusCodes.Status409Conflict,
        OmniRelayStatusCode.PermissionDenied => StatusCodes.Status403Forbidden,
        OmniRelayStatusCode.FailedPrecondition => StatusCodes.Status412PreconditionFailed,
        OmniRelayStatusCode.Aborted => StatusCodes.Status409Conflict,
        OmniRelayStatusCode.OutOfRange => StatusCodes.Status400BadRequest,
        OmniRelayStatusCode.Unimplemented => StatusCodes.Status501NotImplemented,
        OmniRelayStatusCode.Internal => StatusCodes.Status500InternalServerError,
        OmniRelayStatusCode.Unavailable => StatusCodes.Status503ServiceUnavailable,
        OmniRelayStatusCode.DataLoss => StatusCodes.Status500InternalServerError,
        OmniRelayStatusCode.ResourceExhausted => StatusCodes.Status429TooManyRequests,
        OmniRelayStatusCode.DeadlineExceeded => StatusCodes.Status504GatewayTimeout,
        OmniRelayStatusCode.Cancelled => StatusCodes.Status499ClientClosedRequest,
        _ => StatusCodes.Status500InternalServerError
    };

    public static OmniRelayStatusCode FromStatusCode(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => OmniRelayStatusCode.InvalidArgument,
        StatusCodes.Status401Unauthorized => OmniRelayStatusCode.PermissionDenied,
        StatusCodes.Status403Forbidden => OmniRelayStatusCode.PermissionDenied,
        StatusCodes.Status404NotFound => OmniRelayStatusCode.NotFound,
        StatusCodes.Status408RequestTimeout => OmniRelayStatusCode.DeadlineExceeded,
        StatusCodes.Status409Conflict => OmniRelayStatusCode.Aborted,
        StatusCodes.Status412PreconditionFailed => OmniRelayStatusCode.FailedPrecondition,
        StatusCodes.Status415UnsupportedMediaType => OmniRelayStatusCode.InvalidArgument,
        StatusCodes.Status429TooManyRequests => OmniRelayStatusCode.ResourceExhausted,
        StatusCodes.Status500InternalServerError => OmniRelayStatusCode.Internal,
        StatusCodes.Status501NotImplemented => OmniRelayStatusCode.Unimplemented,
        StatusCodes.Status503ServiceUnavailable => OmniRelayStatusCode.Unavailable,
        StatusCodes.Status504GatewayTimeout => OmniRelayStatusCode.DeadlineExceeded,
        _ => OmniRelayStatusCode.Unknown
    };
}
