using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using Grpc.Core;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Errors;

namespace OmniRelay.Transport.Grpc;

/// <summary>
/// Helpers that translate between gRPC metadata and OmniRelay request/response metadata.
/// </summary>
internal static class GrpcMetadataAdapter
{
    private static readonly char[] InvalidHeaderValueCharacters = ['\r', '\n', '\0'];

    /// <summary>
    /// Builds an OmniRelay <see cref="RequestMeta"/> from gRPC metadata.
    /// </summary>
    public static RequestMeta BuildRequestMeta(
        string service,
        string procedure,
        Metadata metadata,
        string? encoding,
        string? protocol = null)
    {
        string? caller = null;
        string? shardKey = null;
        string? routingKey = null;
        string? routingDelegate = null;
        TimeSpan? ttl = null;
        DateTimeOffset? deadline = null;

        ImmutableDictionary<string, string>.Builder? headers = null;

        for (var i = 0; i < metadata.Count; i++)
        {
            var entry = metadata[i];
            if (entry.IsBinary)
            {
                continue;
            }

            headers ??= ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

            var key = entry.Key;
            var value = entry.Value;

            if (caller is null && string.Equals(key, GrpcTransportConstants.CallerHeader, StringComparison.OrdinalIgnoreCase))
            {
                caller = value;
            }
            else if (shardKey is null && string.Equals(key, GrpcTransportConstants.ShardKeyHeader, StringComparison.OrdinalIgnoreCase))
            {
                shardKey = value;
            }
            else if (routingKey is null && string.Equals(key, GrpcTransportConstants.RoutingKeyHeader, StringComparison.OrdinalIgnoreCase))
            {
                routingKey = value;
            }
            else if (routingDelegate is null && string.Equals(key, GrpcTransportConstants.RoutingDelegateHeader, StringComparison.OrdinalIgnoreCase))
            {
                routingDelegate = value;
            }
            else if (ttl is null && string.Equals(key, GrpcTransportConstants.TtlHeader, StringComparison.OrdinalIgnoreCase) &&
                     long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ttlMs))
            {
                ttl = TimeSpan.FromMilliseconds(ttlMs);
            }
            else if (deadline is null && string.Equals(key, GrpcTransportConstants.DeadlineHeader, StringComparison.OrdinalIgnoreCase) &&
                     DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDeadline))
            {
                deadline = parsedDeadline;
            }

            headers[key] = value;
        }

        if (!string.IsNullOrWhiteSpace(protocol))
        {
            headers ??= ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            headers["rpc.protocol"] = protocol!;
        }

        return new RequestMeta
        {
            Service = service,
            Procedure = procedure,
            Caller = caller,
            Encoding = encoding,
            Transport = GrpcTransportConstants.TransportName,
            ShardKey = shardKey,
            RoutingKey = routingKey,
            RoutingDelegate = routingDelegate,
            TimeToLive = ttl,
            Deadline = deadline,
            Headers = headers?.Count > 0
                ? headers.ToImmutable()
                : RequestMeta.EmptyHeadersInstance
        };
    }

    /// <summary>
    /// Creates gRPC request metadata from the OmniRelay <see cref="RequestMeta"/>.
    /// </summary>
    public static Metadata CreateRequestMetadata(RequestMeta meta)
    {
        var metadata = new Metadata();

        var procedureValue = meta.Procedure ?? string.Empty;
        ValidateMetadataValue(GrpcTransportConstants.ProcedureHeader, procedureValue);
        metadata.Add(GrpcTransportConstants.ProcedureHeader, procedureValue);

        var serviceValue = meta.Service ?? string.Empty;
        ValidateMetadataValue(GrpcTransportConstants.ServiceNameHeader, serviceValue);
        metadata.Add(GrpcTransportConstants.ServiceNameHeader, serviceValue);

        if (!string.IsNullOrEmpty(meta.Encoding))
        {
            ValidateMetadataValue(GrpcTransportConstants.EncodingHeader, meta.Encoding);
            metadata.Add(GrpcTransportConstants.EncodingHeader, meta.Encoding);
        }

        if (!string.IsNullOrEmpty(meta.Caller))
        {
            ValidateMetadataValue(GrpcTransportConstants.CallerHeader, meta.Caller);
            metadata.Add(GrpcTransportConstants.CallerHeader, meta.Caller);
        }

        if (!string.IsNullOrEmpty(meta.ShardKey))
        {
            ValidateMetadataValue(GrpcTransportConstants.ShardKeyHeader, meta.ShardKey);
            metadata.Add(GrpcTransportConstants.ShardKeyHeader, meta.ShardKey);
        }

        if (!string.IsNullOrEmpty(meta.RoutingKey))
        {
            ValidateMetadataValue(GrpcTransportConstants.RoutingKeyHeader, meta.RoutingKey);
            metadata.Add(GrpcTransportConstants.RoutingKeyHeader, meta.RoutingKey);
        }

        if (!string.IsNullOrEmpty(meta.RoutingDelegate))
        {
            ValidateMetadataValue(GrpcTransportConstants.RoutingDelegateHeader, meta.RoutingDelegate);
            metadata.Add(GrpcTransportConstants.RoutingDelegateHeader, meta.RoutingDelegate);
        }

        if (meta.TimeToLive is { } ttl)
        {
            var ttlValue = ttl.TotalMilliseconds.ToString(CultureInfo.InvariantCulture);
            ValidateMetadataValue(GrpcTransportConstants.TtlHeader, ttlValue);
            metadata.Add(GrpcTransportConstants.TtlHeader, ttlValue);
        }

        if (meta.Deadline is { } deadline)
        {
            var deadlineValue = deadline.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            ValidateMetadataValue(GrpcTransportConstants.DeadlineHeader, deadlineValue);
            metadata.Add(GrpcTransportConstants.DeadlineHeader, deadlineValue);
        }

        foreach (var header in meta.Headers)
        {
            if (string.Equals(header.Key, GrpcTransportConstants.GrpcEncodingHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ValidateMetadataValue(header.Key, header.Value);
            metadata.Add(header.Key.ToLowerInvariant(), header.Value);
        }

        return metadata;
    }

    /// <summary>
    /// Creates gRPC response headers from the OmniRelay <see cref="ResponseMeta"/>.
    /// </summary>
    public static Metadata CreateResponseHeaders(ResponseMeta meta)
    {
        var headers = new Metadata();
        if (!string.IsNullOrEmpty(meta.Encoding))
        {
            headers.Add(GrpcTransportConstants.EncodingTrailer, meta.Encoding);
        }

        foreach (var header in meta.Headers)
        {
            headers.Add(header.Key.ToLowerInvariant(), header.Value);
        }

        return headers;
    }

    /// <summary>
    /// Combines gRPC headers and trailers into an OmniRelay <see cref="ResponseMeta"/>.
    /// </summary>
    public static ResponseMeta CreateResponseMeta(
        Metadata? headers,
        Metadata? trailers,
        string transport = GrpcTransportConstants.TransportName)
    {
        ImmutableDictionary<string, string>.Builder? headerBuilder = null;
        string? encoding = null;

        if (headers is not null)
        {
            AddMetadata(headers, ref headerBuilder, ref encoding);
        }

        if (trailers is not null)
        {
            AddMetadata(trailers, ref headerBuilder, ref encoding);
        }

        return new ResponseMeta(
            encoding: encoding,
            transport: transport,
            headers: headerBuilder?.Count > 0
                ? headerBuilder.ToImmutable()
                : ResponseMeta.EmptyHeadersInstance);
    }

    private static void AddMetadata(
        Metadata source,
        ref ImmutableDictionary<string, string>.Builder? headerBuilder,
        ref string? encoding)
    {
        if (source.Count == 0)
        {
            return;
        }

        headerBuilder ??= ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < source.Count; i++)
        {
            var entry = source[i];
            if (entry.IsBinary)
            {
                continue;
            }

            if (encoding is null &&
                string.Equals(entry.Key, GrpcTransportConstants.EncodingTrailer, StringComparison.OrdinalIgnoreCase))
            {
                encoding = entry.Value;
            }

            headerBuilder[entry.Key] = entry.Value;
        }
    }

    /// <summary>
    /// Creates gRPC error trailers from an OmniRelay <see cref="Error"/>.
    /// </summary>
    public static Metadata CreateErrorTrailers(Error error)
    {
        var trailers = new Metadata
        {
            { GrpcTransportConstants.ErrorMessageTrailer, error.Message }
        };

        var statusName = OmniRelayErrorAdapter.ToStatus(error).ToString();
        trailers.Add(GrpcTransportConstants.StatusTrailer, statusName);

        if (!string.IsNullOrEmpty(error.Code))
        {
            trailers.Add(GrpcTransportConstants.ErrorCodeTrailer, error.Code);
        }

        if (error.TryGetMetadata(OmniRelayErrorAdapter.TransportMetadataKey, out string? transportValue) &&
            !string.IsNullOrEmpty(transportValue))
        {
            trailers.Add(GrpcTransportConstants.TransportTrailer, transportValue);
        }

        foreach (var kvp in error.Metadata)
        {
            if (string.Equals(kvp.Key, OmniRelayErrorAdapter.TransportMetadataKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            trailers.Add(kvp.Key.ToLowerInvariant(), kvp.Value?.ToString() ?? string.Empty);
        }

        return trailers;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateMetadataValue(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (value.IndexOfAny(InvalidHeaderValueCharacters) >= 0)
        {
            throw new ArgumentException(
                $"gRPC metadata value for '{key}' contains invalid characters (newline or NUL).",
                nameof(value));
        }
    }
}
