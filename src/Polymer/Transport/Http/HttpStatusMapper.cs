using System;
using Microsoft.AspNetCore.Http;
using Polymer.Errors;

namespace Polymer.Transport.Http;

internal static class HttpStatusMapper
{
    public static int ToStatusCode(PolymerStatusCode status) => status switch
    {
        PolymerStatusCode.InvalidArgument => StatusCodes.Status400BadRequest,
        PolymerStatusCode.NotFound => StatusCodes.Status404NotFound,
        PolymerStatusCode.AlreadyExists => StatusCodes.Status409Conflict,
        PolymerStatusCode.PermissionDenied => StatusCodes.Status403Forbidden,
        PolymerStatusCode.FailedPrecondition => StatusCodes.Status412PreconditionFailed,
        PolymerStatusCode.Aborted => StatusCodes.Status409Conflict,
        PolymerStatusCode.OutOfRange => StatusCodes.Status400BadRequest,
        PolymerStatusCode.Unimplemented => StatusCodes.Status501NotImplemented,
        PolymerStatusCode.Internal => StatusCodes.Status500InternalServerError,
        PolymerStatusCode.Unavailable => StatusCodes.Status503ServiceUnavailable,
        PolymerStatusCode.DataLoss => StatusCodes.Status500InternalServerError,
        PolymerStatusCode.ResourceExhausted => StatusCodes.Status429TooManyRequests,
        PolymerStatusCode.DeadlineExceeded => StatusCodes.Status504GatewayTimeout,
        PolymerStatusCode.Cancelled => StatusCodes.Status499ClientClosedRequest,
        _ => StatusCodes.Status500InternalServerError
    };

    public static PolymerStatusCode FromStatusCode(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => PolymerStatusCode.InvalidArgument,
        StatusCodes.Status401Unauthorized => PolymerStatusCode.PermissionDenied,
        StatusCodes.Status403Forbidden => PolymerStatusCode.PermissionDenied,
        StatusCodes.Status404NotFound => PolymerStatusCode.NotFound,
        StatusCodes.Status408RequestTimeout => PolymerStatusCode.DeadlineExceeded,
        StatusCodes.Status409Conflict => PolymerStatusCode.Aborted,
        StatusCodes.Status412PreconditionFailed => PolymerStatusCode.FailedPrecondition,
        StatusCodes.Status415UnsupportedMediaType => PolymerStatusCode.InvalidArgument,
        StatusCodes.Status429TooManyRequests => PolymerStatusCode.ResourceExhausted,
        StatusCodes.Status500InternalServerError => PolymerStatusCode.Internal,
        StatusCodes.Status501NotImplemented => PolymerStatusCode.Unimplemented,
        StatusCodes.Status503ServiceUnavailable => PolymerStatusCode.Unavailable,
        StatusCodes.Status504GatewayTimeout => PolymerStatusCode.DeadlineExceeded,
        _ => PolymerStatusCode.Unknown
    };
}
