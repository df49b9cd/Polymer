using System;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Runtime.CompilerServices;
using Grpc.Core;
using Hugo;
using Polymer.Core;
using Polymer.Errors;

namespace Polymer.Transport.Grpc;

internal static class GrpcMetadataAdapter
{
    private static readonly char[] InvalidHeaderValueCharacters = ['\r', '\n', '\0'];

    public static RequestMeta BuildRequestMeta(
        string service,
        string procedure,
        Metadata metadata,
        string? encoding)
    {
        var headers = metadata
            .Where(static entry => !entry.IsBinary)
            .Select(static entry => new KeyValuePair<string, string>(entry.Key, entry.Value));

        var caller = metadata.GetValue(GrpcTransportConstants.CallerHeader);
        var shardKey = metadata.GetValue(GrpcTransportConstants.ShardKeyHeader);
        var routingKey = metadata.GetValue(GrpcTransportConstants.RoutingKeyHeader);
        var routingDelegate = metadata.GetValue(GrpcTransportConstants.RoutingDelegateHeader);

        TimeSpan? ttl = null;
        var ttlValue = metadata.GetValue(GrpcTransportConstants.TtlHeader);
        if (!string.IsNullOrEmpty(ttlValue) &&
            long.TryParse(ttlValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ttlMs))
        {
            ttl = TimeSpan.FromMilliseconds(ttlMs);
        }

        DateTimeOffset? deadline = null;
        var deadlineValue = metadata.GetValue(GrpcTransportConstants.DeadlineHeader);
        if (!string.IsNullOrEmpty(deadlineValue) &&
            DateTimeOffset.TryParse(deadlineValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDeadline))
        {
            deadline = parsedDeadline;
        }

        return new RequestMeta(
            service,
            procedure,
            caller: caller,
            encoding: encoding,
            transport: GrpcTransportConstants.TransportName,
            shardKey: shardKey,
            routingKey: routingKey,
            routingDelegate: routingDelegate,
            timeToLive: ttl,
            deadline: deadline,
            headers: headers);
    }

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
            ValidateMetadataValue(header.Key, header.Value);
            metadata.Add(header.Key.ToLowerInvariant(), header.Value);
        }

        return metadata;
    }

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

    public static ResponseMeta CreateResponseMeta(
        Metadata? headers,
        Metadata? trailers,
        string transport = GrpcTransportConstants.TransportName)
    {
        headers ??= [];
        trailers ??= [];

        var combined = headers
            .Concat(trailers)
            .Where(static entry => !entry.IsBinary)
            .Select(static entry => new KeyValuePair<string, string>(entry.Key, entry.Value));

        var encoding = headers.GetValue(GrpcTransportConstants.EncodingTrailer)
            ?? trailers.GetValue(GrpcTransportConstants.EncodingTrailer);

        return new ResponseMeta(
            encoding: encoding,
            transport: transport,
            headers: combined);
    }

    public static Metadata CreateErrorTrailers(Error error)
    {
        var trailers = new Metadata
        {
            { GrpcTransportConstants.ErrorMessageTrailer, error.Message }
        };

        if (!string.IsNullOrEmpty(error.Code))
        {
            trailers.Add(GrpcTransportConstants.ErrorCodeTrailer, error.Code);
        }

        var statusName = PolymerErrorAdapter.ToStatus(error).ToString();
        trailers.Add(GrpcTransportConstants.StatusTrailer, statusName);

        foreach (var kvp in error.Metadata)
        {
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
