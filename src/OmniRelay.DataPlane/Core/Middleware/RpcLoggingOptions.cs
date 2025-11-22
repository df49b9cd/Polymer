using System.Diagnostics;
using Hugo;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Core.Middleware;

/// <summary>
/// Options for RPC logging middleware such as log levels, predicates, and scope enrichment.
/// </summary>
public sealed class RpcLoggingOptions
{
    /// <summary>Minimum level used when logging successful calls.</summary>
    public LogLevel SuccessLogLevel { get; init; } = LogLevel.Information;
    /// <summary>Minimum level used when logging failed calls and exceptions.</summary>
    public LogLevel FailureLogLevel { get; init; } = LogLevel.Warning;
    /// <summary>Predicate to decide if a request should be logged.</summary>
    public Func<RequestMeta, bool>? ShouldLogRequest { get; init; }
    /// <summary>Predicate to decide if an error should be logged when the request wasn’t logged.</summary>
    public Func<Error, bool>? ShouldLogError { get; init; }
    /// <summary>Enrichment callback to add scoped properties to logs.</summary>
    public Func<RequestMeta, ResponseMeta?, Activity?, IEnumerable<KeyValuePair<string, object?>>>? Enrich { get; init; }
}
