namespace OmniRelay.Dispatcher.Config;

// Lightweight transport-policy stubs to keep CLI/tests compiling after removing the legacy configuration package.

public sealed class OmniRelayConfigurationOptions
{
    public string? Service { get; set; }
    public DiagnosticsConfiguration Diagnostics { get; set; } = new();
    public TransportPolicyConfiguration TransportPolicy { get; set; } = new();
    public LoggingConfiguration Logging { get; set; } = new();
}

public sealed class DiagnosticsConfiguration
{
    public DiagnosticsControlPlaneConfiguration ControlPlane { get; set; } = new();
    public DiagnosticsOpenTelemetryConfiguration OpenTelemetry { get; set; } = new();
}

public sealed class DiagnosticsControlPlaneConfiguration
{
    public List<string> HttpUrls { get; set; } = new();
    public List<string> GrpcUrls { get; set; } = new();
    public ControlPlaneRuntimeConfiguration HttpRuntime { get; set; } = new();
    public ControlPlaneRuntimeConfiguration GrpcRuntime { get; set; } = new();
}

public sealed class ControlPlaneRuntimeConfiguration
{
    public bool EnableHttp3 { get; set; }
}

public sealed class DiagnosticsOpenTelemetryConfiguration
{
    public bool? Enabled { get; set; }
}

public sealed class LoggingConfiguration
{
    public string? Level { get; set; }
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TransportPolicyConfiguration
{
    public List<TransportPolicyExceptionConfiguration> Exceptions { get; set; } = new();
}

public sealed class TransportPolicyExceptionConfiguration
{
    public string? Name { get; set; }
    public TransportPolicyCategories Category { get; set; }
    public List<TransportPolicyEndpoints> AppliesTo { get; set; } = new();
    public List<TransportPolicyTransports> Transports { get; set; } = new();
    public List<TransportPolicyEncodings> Encodings { get; set; } = new();
    public string? Reason { get; set; }
    public DateTimeOffset? ExpiresAfter { get; set; }
}

public enum TransportPolicyCategories { Diagnostics }
public enum TransportPolicyEndpoints { DiagnosticsHttp, DiagnosticsGrpc }
public enum TransportPolicyTransports { Http2, Http3 }
public enum TransportPolicyEncodings { Json, Protobuf }

public readonly struct TransportPolicyFinding
{
    public TransportPolicyFinding(
        TransportPolicyFindingStatus status,
        string message,
        string? endpoint = null,
        TransportPolicyCategories category = TransportPolicyCategories.Diagnostics,
        TransportPolicyEndpoints appliesTo = TransportPolicyEndpoints.DiagnosticsHttp,
        TransportPolicyTransports transport = TransportPolicyTransports.Http2,
        TransportPolicyEncodings encoding = TransportPolicyEncodings.Json,
        bool http3Enabled = false,
        string? hint = null,
        string? exceptionName = null,
        string? exceptionReason = null,
        DateTimeOffset? exceptionExpiresAfter = null)
    {
        Status = status;
        Message = message;
        Endpoint = endpoint ?? string.Empty;
        Category = category;
        AppliesTo = appliesTo;
        Transport = transport;
        Encoding = encoding;
        Http3Enabled = http3Enabled;
        Hint = hint;
        ExceptionName = exceptionName;
        ExceptionReason = exceptionReason;
        ExceptionExpiresAfter = exceptionExpiresAfter;
    }

    public TransportPolicyFindingStatus Status { get; }
    public string Message { get; }
    public string Endpoint { get; }
    public TransportPolicyCategories Category { get; }
    public TransportPolicyEndpoints AppliesTo { get; }
    public TransportPolicyTransports Transport { get; }
    public TransportPolicyEncodings Encoding { get; }
    public bool Http3Enabled { get; }
    public string? Hint { get; }
    public string? ExceptionName { get; }
    public string? ExceptionReason { get; }
    public DateTimeOffset? ExceptionExpiresAfter { get; }
}

public sealed class TransportPolicyEvaluationSummary
{
    public int Total { get; set; }
    public int Compliant { get; set; }
    public int Excepted { get; set; }
    public int Violations { get; set; }
}

public readonly struct TransportPolicyEvaluationResult
{
    public TransportPolicyEvaluationResult(bool isAllowed, IReadOnlyList<TransportPolicyFinding>? findings = null, TransportPolicyEvaluationSummary? summary = null)
    {
        IsAllowed = isAllowed;
        Findings = findings ?? Array.Empty<TransportPolicyFinding>();
        Summary = summary ?? new TransportPolicyEvaluationSummary();
    }

    public bool IsAllowed { get; }
    public IReadOnlyList<TransportPolicyFinding> Findings { get; }
    public TransportPolicyEvaluationSummary Summary { get; }
    public bool HasViolations => Findings.Any(f => f.Status == TransportPolicyFindingStatus.Violation);
    public bool HasExceptions => Findings.Any(f => f.Status == TransportPolicyFindingStatus.Excepted);
}

public enum TransportPolicyFindingStatus
{
    Compliant,
    Violation,
    Excepted
}

public static class TransportPolicyEvaluator
{
    public static TransportPolicyEvaluationResult Evaluate(object? optionsObj)
    {
        var options = optionsObj as OmniRelayConfigurationOptions ?? new OmniRelayConfigurationOptions();
        var findings = new List<TransportPolicyFinding>();

        void AddEndpointFinding(string endpoint, bool http3, TransportPolicyEndpoints appliesTo, TransportPolicyTransports transport)
        {
            findings.Add(new TransportPolicyFinding(
                TransportPolicyFindingStatus.Compliant,
                message: "Endpoint meets current transport policy.",
                endpoint: endpoint,
                category: TransportPolicyCategories.Diagnostics,
                appliesTo: appliesTo,
                transport: transport,
                encoding: TransportPolicyEncodings.Json,
                http3Enabled: http3));
        }

        foreach (var http in options.Diagnostics.ControlPlane.HttpUrls)
        {
            AddEndpointFinding(http, options.Diagnostics.ControlPlane.HttpRuntime.EnableHttp3, TransportPolicyEndpoints.DiagnosticsHttp, options.Diagnostics.ControlPlane.HttpRuntime.EnableHttp3 ? TransportPolicyTransports.Http3 : TransportPolicyTransports.Http2);
        }

        foreach (var grpc in options.Diagnostics.ControlPlane.GrpcUrls)
        {
            AddEndpointFinding(grpc, options.Diagnostics.ControlPlane.GrpcRuntime.EnableHttp3, TransportPolicyEndpoints.DiagnosticsGrpc, options.Diagnostics.ControlPlane.GrpcRuntime.EnableHttp3 ? TransportPolicyTransports.Http3 : TransportPolicyTransports.Http2);
        }

        var summary = new TransportPolicyEvaluationSummary
        {
            Total = findings.Count,
            Compliant = findings.Count(f => f.Status == TransportPolicyFindingStatus.Compliant),
            Excepted = findings.Count(f => f.Status == TransportPolicyFindingStatus.Excepted),
            Violations = findings.Count(f => f.Status == TransportPolicyFindingStatus.Violation)
        };

        var isAllowed = summary.Violations == 0;
        return new TransportPolicyEvaluationResult(isAllowed, findings, summary);
    }

    public static TransportPolicyEvaluationResult Enforce(object? options) => Evaluate(options);
}
