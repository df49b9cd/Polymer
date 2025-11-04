using Grpc.Core;
using OmniRelay.Errors;

namespace OmniRelay.Transport.Grpc;

internal static class GrpcStatusMapper
{
    public static OmniRelayStatusCode FromStatus(Status status) => status.StatusCode switch
    {
        StatusCode.Cancelled => OmniRelayStatusCode.Cancelled,
        StatusCode.Unknown => OmniRelayStatusCode.Unknown,
        StatusCode.InvalidArgument => OmniRelayStatusCode.InvalidArgument,
        StatusCode.DeadlineExceeded => OmniRelayStatusCode.DeadlineExceeded,
        StatusCode.NotFound => OmniRelayStatusCode.NotFound,
        StatusCode.AlreadyExists => OmniRelayStatusCode.AlreadyExists,
        StatusCode.PermissionDenied => OmniRelayStatusCode.PermissionDenied,
        StatusCode.ResourceExhausted => OmniRelayStatusCode.ResourceExhausted,
        StatusCode.FailedPrecondition => OmniRelayStatusCode.FailedPrecondition,
        StatusCode.Aborted => OmniRelayStatusCode.Aborted,
        StatusCode.OutOfRange => OmniRelayStatusCode.OutOfRange,
        StatusCode.Unimplemented => OmniRelayStatusCode.Unimplemented,
        StatusCode.Internal => OmniRelayStatusCode.Internal,
        StatusCode.Unavailable => OmniRelayStatusCode.Unavailable,
        StatusCode.DataLoss => OmniRelayStatusCode.DataLoss,
        StatusCode.Unauthenticated => OmniRelayStatusCode.PermissionDenied,
        _ => OmniRelayStatusCode.Unknown
    };

    public static Status ToStatus(OmniRelayStatusCode statusCode, string message) => statusCode switch
    {
        OmniRelayStatusCode.Cancelled => new Status(StatusCode.Cancelled, message),
        OmniRelayStatusCode.InvalidArgument => new Status(StatusCode.InvalidArgument, message),
        OmniRelayStatusCode.DeadlineExceeded => new Status(StatusCode.DeadlineExceeded, message),
        OmniRelayStatusCode.NotFound => new Status(StatusCode.NotFound, message),
        OmniRelayStatusCode.AlreadyExists => new Status(StatusCode.AlreadyExists, message),
        OmniRelayStatusCode.PermissionDenied => new Status(StatusCode.PermissionDenied, message),
        OmniRelayStatusCode.ResourceExhausted => new Status(StatusCode.ResourceExhausted, message),
        OmniRelayStatusCode.FailedPrecondition => new Status(StatusCode.FailedPrecondition, message),
        OmniRelayStatusCode.Aborted => new Status(StatusCode.Aborted, message),
        OmniRelayStatusCode.OutOfRange => new Status(StatusCode.OutOfRange, message),
        OmniRelayStatusCode.Unimplemented => new Status(StatusCode.Unimplemented, message),
        OmniRelayStatusCode.Internal => new Status(StatusCode.Internal, message),
        OmniRelayStatusCode.Unavailable => new Status(StatusCode.Unavailable, message),
        OmniRelayStatusCode.DataLoss => new Status(StatusCode.DataLoss, message),
        OmniRelayStatusCode.Unknown => new Status(StatusCode.Unknown, message),
        _ => new Status(StatusCode.Unknown, message)
    };
}
