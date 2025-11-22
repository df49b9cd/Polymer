using System.Collections.Immutable;

namespace OmniRelay.Core;

/// <summary>
/// Encapsulates metadata surfaced alongside an RPC response.
/// </summary>
public sealed record ResponseMeta
{
    private static readonly ImmutableDictionary<string, string> EmptyHeaders =
        ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase);

    internal static ImmutableDictionary<string, string> EmptyHeadersInstance => EmptyHeaders;
    public static ImmutableDictionary<string, string> EmptyHeadersPublic => EmptyHeaders;

    public string? Encoding { get; init; }
    public string? Transport { get; init; }
    public TimeSpan? Ttl { get; init; }
    public ImmutableDictionary<string, string> Headers { get; init; } = EmptyHeaders;

    public ResponseMeta()
    {
    }

    public ResponseMeta(
        string? encoding = null,
        string? transport = null,
        TimeSpan? ttl = null,
        IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        Encoding = encoding;
        Transport = transport;
        Ttl = ttl;
        Headers = headers is null
            ? EmptyHeaders
            : ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, headers) ?? EmptyHeaders;
    }

    public ResponseMeta WithHeader(string key, string value)
    {
        var updated = Headers.SetItem(key, value);
#pragma warning disable CS8601
        return CopyWithHeaders(updated);
#pragma warning restore CS8601
    }

    public ResponseMeta WithHeaders(IEnumerable<KeyValuePair<string, string>> headers)
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

    private ResponseMeta CopyWithHeaders(ImmutableDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        return new ResponseMeta(
            encoding: Encoding,
            transport: Transport,
            ttl: Ttl,
            headers: headers);
    }

    public bool TryGetHeader(string key, out string? value) =>
        Headers.TryGetValue(key, out value);
}
