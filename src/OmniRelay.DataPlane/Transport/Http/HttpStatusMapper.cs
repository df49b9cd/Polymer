using Microsoft.AspNetCore.Http;
using OmniRelay.Errors;

namespace OmniRelay.Transport.Http;

/// <summary>
/// Maps between OmniRelay status codes and HTTP status codes for inbound and outbound translation.
/// </summary>
internal static class HttpStatusMapper
{
    /// <summary>
    /// Converts an <see cref="OmniRelayStatusCode"/> to the corresponding HTTP status code for responses.
    /// </summary>
    /// <param name="status">The OmniRelay status.</param>
    /// <returns>An HTTP status code.</returns>
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

    /// <summary>
    /// Converts an HTTP status code to an <see cref="OmniRelayStatusCode"/> for error normalization.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>The corresponding OmniRelay status code.</returns>
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
