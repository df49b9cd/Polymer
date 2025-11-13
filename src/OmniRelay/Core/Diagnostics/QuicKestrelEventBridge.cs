using System.Diagnostics.Tracing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Core.Diagnostics;

/// <summary>
/// Bridges QUIC/MsQuic and Kestrel EventSource events into structured logs so operators can observe
/// connection lifecycle (handshake failures, migration, congestion) without external ETW tooling.
/// </summary>
internal sealed class QuicKestrelEventBridge(ILogger<QuicKestrelEventBridge> logger) : EventListener
{
    private readonly ILogger<QuicKestrelEventBridge> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private const string MsQuicEventSource = "Private.InternalDiagnostics.System.Net.Quic";
    private const string KestrelEventSource = "Microsoft-AspNetCore-Server-Kestrel";
    private static readonly Action<ILogger, string, Exception?> HandshakeFailureLog =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1000, "QuicHandshakeFailure"), "quic event: category={Category}");
    private static readonly Action<ILogger, string, Exception?> InformationalLog =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1001, "QuicInfoEvent"), "quic event: category={Category}");
    private static readonly Action<ILogger, string, Exception?> DebugLog =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1002, "QuicDebugEvent"), "quic event: category={Category}");

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        base.OnEventSourceCreated(eventSource);

        if (eventSource.Name.Equals(MsQuicEventSource, StringComparison.Ordinal) ||
            eventSource.Name.Equals(KestrelEventSource, StringComparison.Ordinal))
        {
            // Enable informational+ events; adjust keywords as needed for verbosity
            EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData is null)
        {
            return;
        }

        try
        {
            var source = eventData.EventSource?.Name ?? "unknown";
            var name = eventData.EventName ?? eventData.EventId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["provider"] = source,
                ["event"] = name,
                ["event_id"] = eventData.EventId,
                ["level"] = eventData.Level.ToString()
            };

            if (eventData.Payload is { Count: > 0 })
            {
                var hasNames = eventData.PayloadNames is { Count: > 0 };
                for (var i = 0; i < eventData.Payload.Count; i++)
                {
                    var key = hasNames && i < eventData.PayloadNames!.Count ? eventData.PayloadNames[i] : null;
                    var value = eventData.Payload[i];
                    key = string.IsNullOrWhiteSpace(key) ? $"arg{i}" : key;
                    payload[key] = value;
                }
            }

            // Derive a coarse category for common lifecycle events to assist filtering
            var category = ClassifyEvent(source, name, payload);

            using var scope = _logger.BeginScope(payload);

            switch (category)
            {
                case "handshake_failure":
                    HandshakeFailureLog(_logger, category, null);
                    break;
                case "migration":
                case "congestion":
                    InformationalLog(_logger, category, null);
                    break;
                default:
                    DebugLog(_logger, category, null);
                    break;
            }
        }
        catch
        {
            // Swallow logging errors to avoid impacting application flow
        }
    }

    private static string ClassifyEvent(string provider, string name, IReadOnlyDictionary<string, object?> payload)
    {
        // Heuristic classification based on common keywords
        var key = provider + ":" + name;

        if (Contains(name, "handshake") && (Contains(name, "fail") || Contains(name, "error")))
        {
            return "handshake_failure";
        }

        if (Contains(name, "pathvalidated") || Contains(name, "migration"))
        {
            return "migration";
        }

        var text = string.Join(' ', payload.Select(kv => kv.Key + "=" + (kv.Value?.ToString() ?? string.Empty))).ToLowerInvariant();

        if (text.Contains("handshake") && (text.Contains("fail") || text.Contains("error")))
        {
            return "handshake_failure";
        }

        if (text.Contains("migrate") || text.Contains("path_validated"))
        {
            return "migration";
        }

        if (text.Contains("congestion") || text.Contains("loss") || text.Contains("retransmit"))
        {
            return "congestion";
        }

        if (key.Contains("Kestrel", StringComparison.OrdinalIgnoreCase) && name.Contains("Http3", StringComparison.OrdinalIgnoreCase))
        {
            return "http3";
        }

        return "other";
    }

    private static bool Contains(string source, string value) =>
        source.Contains(value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Hosted service to control the lifetime of the QuicKestrelEventBridge.
/// </summary>
internal sealed class QuicDiagnosticsHostedService(ILogger<QuicKestrelEventBridge> logger) : IHostedService, IDisposable
{
    private readonly QuicKestrelEventBridge _bridge = new QuicKestrelEventBridge(logger);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _bridge.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _bridge.Dispose();
        GC.SuppressFinalize(this);
    }
}
