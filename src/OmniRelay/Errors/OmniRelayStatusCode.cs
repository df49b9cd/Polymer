namespace OmniRelay.Errors;

/// <summary>
/// Canonical status codes for OmniRelay error semantics.
/// </summary>
[Flags]
public enum OmniRelayStatusCode
{
    Unknown = 0,
    Cancelled = 1,
    InvalidArgument = 2,
    DeadlineExceeded = 3,
    NotFound = 4,
    AlreadyExists = 5,
    PermissionDenied = 6,
    ResourceExhausted = 7,
    FailedPrecondition = 8,
    Aborted = 9,
    OutOfRange = 10,
    Unimplemented = 12,
    Internal = 13,
    Unavailable = 14,
    DataLoss = 15
}
