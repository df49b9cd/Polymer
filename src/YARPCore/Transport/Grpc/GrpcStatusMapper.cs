using Grpc.Core;
using YARPCore.Errors;

namespace YARPCore.Transport.Grpc;

internal static class GrpcStatusMapper
{
    public static PolymerStatusCode FromStatus(Status status) => status.StatusCode switch
    {
        StatusCode.Cancelled => PolymerStatusCode.Cancelled,
        StatusCode.Unknown => PolymerStatusCode.Unknown,
        StatusCode.InvalidArgument => PolymerStatusCode.InvalidArgument,
        StatusCode.DeadlineExceeded => PolymerStatusCode.DeadlineExceeded,
        StatusCode.NotFound => PolymerStatusCode.NotFound,
        StatusCode.AlreadyExists => PolymerStatusCode.AlreadyExists,
        StatusCode.PermissionDenied => PolymerStatusCode.PermissionDenied,
        StatusCode.ResourceExhausted => PolymerStatusCode.ResourceExhausted,
        StatusCode.FailedPrecondition => PolymerStatusCode.FailedPrecondition,
        StatusCode.Aborted => PolymerStatusCode.Aborted,
        StatusCode.OutOfRange => PolymerStatusCode.OutOfRange,
        StatusCode.Unimplemented => PolymerStatusCode.Unimplemented,
        StatusCode.Internal => PolymerStatusCode.Internal,
        StatusCode.Unavailable => PolymerStatusCode.Unavailable,
        StatusCode.DataLoss => PolymerStatusCode.DataLoss,
        StatusCode.Unauthenticated => PolymerStatusCode.PermissionDenied,
        _ => PolymerStatusCode.Unknown
    };

    public static Status ToStatus(PolymerStatusCode statusCode, string message) => statusCode switch
    {
        PolymerStatusCode.Cancelled => new Status(StatusCode.Cancelled, message),
        PolymerStatusCode.InvalidArgument => new Status(StatusCode.InvalidArgument, message),
        PolymerStatusCode.DeadlineExceeded => new Status(StatusCode.DeadlineExceeded, message),
        PolymerStatusCode.NotFound => new Status(StatusCode.NotFound, message),
        PolymerStatusCode.AlreadyExists => new Status(StatusCode.AlreadyExists, message),
        PolymerStatusCode.PermissionDenied => new Status(StatusCode.PermissionDenied, message),
        PolymerStatusCode.ResourceExhausted => new Status(StatusCode.ResourceExhausted, message),
        PolymerStatusCode.FailedPrecondition => new Status(StatusCode.FailedPrecondition, message),
        PolymerStatusCode.Aborted => new Status(StatusCode.Aborted, message),
        PolymerStatusCode.OutOfRange => new Status(StatusCode.OutOfRange, message),
        PolymerStatusCode.Unimplemented => new Status(StatusCode.Unimplemented, message),
        PolymerStatusCode.Internal => new Status(StatusCode.Internal, message),
        PolymerStatusCode.Unavailable => new Status(StatusCode.Unavailable, message),
        PolymerStatusCode.DataLoss => new Status(StatusCode.DataLoss, message),
        PolymerStatusCode.Unknown => new Status(StatusCode.Unknown, message),
        _ => new Status(StatusCode.Unknown, message)
    };
}
