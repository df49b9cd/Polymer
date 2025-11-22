using System.Collections.Immutable;

namespace OmniRelay.Core;

/// <summary>
/// Carries transport-agnostic metadata for an RPC request.
/// </summary>
public sealed record RequestMeta
{
    private static readonly ImmutableDictionary<string, string> EmptyHeaders =
        ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase);

    internal static ImmutableDictionary<string, string> EmptyHeadersInstance => EmptyHeaders;
    public static ImmutableDictionary<string, string> EmptyHeadersPublic => EmptyHeaders;

    public string Service { get; init; } = string.Empty;

    public string? Procedure { get; init; }

    public string? Caller { get; init; }

    public string? Encoding { get; init; }

    public string? Transport { get; init; }

    public string? ShardKey { get; init; }

    public string? RoutingKey { get; init; }

    public string? RoutingDelegate { get; init; }

    public TimeSpan? TimeToLive { get; init; }

    public DateTimeOffset? Deadline { get; init; }

    public ImmutableDictionary<string, string> Headers { get; init; } = EmptyHeaders;

    public RequestMeta()
    {
    }

    public RequestMeta(
        string service,
        string? procedure = null,
        string? caller = null,
        string? encoding = null,
        string? transport = null,
        string? shardKey = null,
        string? routingKey = null,
        string? routingDelegate = null,
        TimeSpan? timeToLive = null,
        DateTimeOffset? deadline = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        Service = service ?? string.Empty;
        Procedure = procedure;
        Caller = caller;
        Encoding = encoding;
        Transport = transport;
        ShardKey = shardKey;
        RoutingKey = routingKey;
        RoutingDelegate = routingDelegate;
        TimeToLive = timeToLive;
        Deadline = deadline;
        Headers = headers is null
            ? EmptyHeaders
            : ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, headers) ?? EmptyHeaders;
    }

    public RequestMeta WithHeader(string key, string value)
    {
        var updated = Headers.SetItem(key, value);
#pragma warning disable CS8601
        return CopyWithHeaders(updated);
#pragma warning restore CS8601
    }

    public RequestMeta WithHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        builder.AddRange(Headers);

        foreach (var kvp in headers)
        {
            builder[kvp.Key] = kvp.Value;
        }

        var merged = builder.ToImmutable();
#pragma warning disable CS8601
        return CopyWithHeaders(merged);
#pragma warning restore CS8601
    }

    private RequestMeta CopyWithHeaders(ImmutableDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        return new RequestMeta(
            service: Service,
            procedure: Procedure,
            caller: Caller,
            encoding: Encoding,
            transport: Transport,
            shardKey: ShardKey,
            routingKey: RoutingKey,
            routingDelegate: RoutingDelegate,
            timeToLive: TimeToLive,
            deadline: Deadline,
            headers: headers);
    }

    public bool TryGetHeader(string key, out string? value) =>
        Headers.TryGetValue(key, out value);
}
