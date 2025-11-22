using System.Diagnostics;
using System.Net;
using Grpc.Core;
using Hugo;

namespace OmniRelay.Transport.Grpc;

internal static class GrpcTransportDiagnostics
{
    public const string ActivitySourceName = "OmniRelay.Transport.Grpc";

    private static readonly string InstrumentationVersion =
        typeof(GrpcTransportDiagnostics).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    private static readonly ActivitySource ActivitySource =
        GoDiagnostics.CreateActivitySource(ActivitySourceName, InstrumentationVersion, (string?)null);

    public static Activity? StartClientActivity(string remoteService, string procedure, Uri address, string operation)
    {
        if (!ActivitySource.HasListeners())
        {
            return null;
        }

        var activityName = $"grpc.client.{operation}";
        var activity = ActivitySource.StartActivity(activityName, ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        PopulateCommonClientTags(activity, remoteService, procedure, address);
        return activity;
    }

    public static Activity? StartServerActivity(
        string service,
        string procedure,
        Metadata metadata,
        ServerCallContext callContext,
        string operation)
    {
        var parentContext = ExtractParentContext(metadata);

        Activity? activity;
        if (parentContext.HasValue)
        {
            activity = ActivitySource.StartActivity(
                $"grpc.server.{operation}",
                ActivityKind.Server,
                parentContext.Value);
        }
        else
        {
            activity = ActivitySource.StartActivity($"grpc.server.{operation}", ActivityKind.Server);
        }

        if (activity is null)
        {
            return null;
        }

        PopulateCommonServerTags(activity, service, procedure, callContext);
        return activity;
    }

    public static void SetStatus(Activity? activity, StatusCode statusCode, string? description = null)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("rpc.grpc.status_code", (int)statusCode);
        if (statusCode == StatusCode.OK)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Error, description);
        }
    }

    public static void RecordException(Activity? activity, Exception exception, StatusCode statusCode, string? description = null)
    {
        if (activity is null)
        {
            return;
        }

        var tags = new ActivityTagsCollection
        {
            { "exception.type", exception.GetType().FullName ?? exception.GetType().Name },
            { "exception.message", exception.Message }
        };

        if (!string.IsNullOrEmpty(exception.StackTrace))
        {
            tags.Add("exception.stacktrace", exception.StackTrace);
        }

        activity.AddEvent(new ActivityEvent("exception", tags: tags));
        SetStatus(activity, statusCode, description ?? exception.Message);
    }

    private static void PopulateCommonClientTags(Activity activity, string remoteService, string procedure, Uri address)
    {
        activity.SetTag("rpc.system", "grpc");
        activity.SetTag("rpc.service", remoteService);
        activity.SetTag("rpc.method", procedure);

        if (!string.IsNullOrEmpty(address.Host))
        {
            if (IPAddress.TryParse(address.Host, out var ipAddress))
            {
                activity.SetTag("net.peer.ip", ipAddress.ToString());
            }
            else
            {
                activity.SetTag("net.peer.name", address.Host);
            }
        }

        if (address.Port > 0)
        {
            activity.SetTag("net.peer.port", address.Port);
        }
    }

    private static void PopulateCommonServerTags(Activity activity, string service, string procedure, ServerCallContext context)
    {
        activity.SetTag("rpc.system", "grpc");
        activity.SetTag("rpc.service", service);
        activity.SetTag("rpc.method", procedure);

        var httpContext = context.GetHttpContext();
        if (httpContext is not null)
        {
            var (protocolName, protocolVersion) = ParseHttpProtocol(httpContext.Request.Protocol);
            activity.SetTag("rpc.protocol", httpContext.Request.Protocol);
            if (!string.IsNullOrEmpty(protocolName))
            {
                activity.SetTag("network.protocol.name", protocolName);
            }

            if (!string.IsNullOrEmpty(protocolVersion))
            {
                activity.SetTag("network.protocol.version", protocolVersion);
            }

            // Map transport for HTTP variants. HTTP/3 implies QUIC (UDP). Others are typically TCP.
            if (string.Equals(protocolName, "http", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(protocolVersion, "3", StringComparison.Ordinal))
                {
                    activity.SetTag("network.transport", "quic");
                }
                else
                {
                    activity.SetTag("network.transport", "tcp");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(context.Peer))
        {
            var peer = context.Peer;
            if (peer.StartsWith("ipv4:", StringComparison.OrdinalIgnoreCase) ||
                peer.StartsWith("ipv6:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = peer.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    activity.SetTag("net.peer.ip", parts[1]);
                    if (int.TryParse(parts[2], out var port))
                    {
                        activity.SetTag("net.peer.port", port);
                    }
                }
            }
            else
            {
                activity.SetTag("net.peer.name", peer);
            }
        }
    }

    private static ActivityContext? ExtractParentContext(Metadata metadata)
    {
        var traceParent = metadata.GetValue("traceparent");
        if (string.IsNullOrWhiteSpace(traceParent))
        {
            return null;
        }

        var traceState = metadata.GetValue("tracestate");
        return ActivityContext.TryParse(traceParent, traceState, out var context) ? context : null;
    }

    private static (string? Name, string? Version) ParseHttpProtocol(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
        {
            return (null, null);
        }

        if (protocol.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            var version = protocol[5..];
            return ("http", string.IsNullOrWhiteSpace(version) ? null : version);
        }

        return (protocol.ToLowerInvariant(), null);
    }
}
