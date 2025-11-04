using System.Diagnostics;
using Hugo;
using Microsoft.Extensions.Logging;

namespace YARPCore.Core.Middleware;

public sealed class RpcLoggingOptions
{
    public LogLevel SuccessLogLevel { get; init; } = LogLevel.Information;
    public LogLevel FailureLogLevel { get; init; } = LogLevel.Warning;
    public Func<RequestMeta, bool>? ShouldLogRequest { get; init; }
    public Func<Error, bool>? ShouldLogError { get; init; }
    public Func<RequestMeta, ResponseMeta?, Activity?, IEnumerable<KeyValuePair<string, object?>>>? Enrich { get; init; }
}
