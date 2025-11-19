using System.Collections.ObjectModel;
using OmniRelay.Configuration.Models;

namespace OmniRelay.Configuration.Internal.TransportPolicy;

internal static class TransportPolicyEvaluator
{
    public static TransportPolicyEvaluationResult Evaluate(
        OmniRelayConfigurationOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var policy = options.TransportPolicy ?? new TransportPolicyConfiguration();
        var endpoints = BuildEndpoints(options);
        var findings = new List<TransportPolicyFinding>();
        timeProvider ??= TimeProvider.System;
        foreach (var endpoint in endpoints)
        {
            var finding = EvaluateEndpoint(policy, endpoint, timeProvider);
            findings.Add(finding);
            TransportPolicyMetrics.RecordEvaluation(finding);
        }

        return new TransportPolicyEvaluationResult(findings);
    }

    public static void Enforce(OmniRelayConfigurationOptions options, TimeProvider? timeProvider = null)
    {
        var result = Evaluate(options, timeProvider);
        var violation = result.Findings.FirstOrDefault(static finding => finding.Status == TransportPolicyFindingStatus.ViolatesPolicy);
        if (violation is null)
        {
            return;
        }

        throw new OmniRelayConfigurationException(
            $"Transport policy violation for '{violation.Endpoint}' ({violation.Category}): {violation.Message}");
    }

    private static IEnumerable<TransportPolicyEndpointDescriptor> BuildEndpoints(OmniRelayConfigurationOptions options)
    {
        var diagnostics = options.Diagnostics ?? new DiagnosticsConfiguration();
        var controlPlane = diagnostics.ControlPlane ?? new DiagnosticsControlPlaneConfiguration();

        if (controlPlane.HttpUrls.Count > 0)
        {
            var http3Enabled = controlPlane.HttpRuntime?.EnableHttp3 ?? false;
            var transport = http3Enabled ? TransportPolicyTransports.Http3 : TransportPolicyTransports.Http2;
            yield return new TransportPolicyEndpointDescriptor(
                TransportPolicyEndpoints.DiagnosticsHttp,
                TransportPolicyCategories.Diagnostics,
                transport,
                TransportPolicyEncodings.Json,
                http3Enabled,
                "Set diagnostics.controlPlane.httpRuntime.enableHttp3 = true to satisfy HTTP/3-first policy.");
        }

        if (controlPlane.GrpcUrls.Count > 0)
        {
            var http3Enabled = controlPlane.GrpcRuntime?.EnableHttp3 ?? false;
            yield return new TransportPolicyEndpointDescriptor(
                TransportPolicyEndpoints.ControlPlaneGrpc,
                TransportPolicyCategories.ControlPlane,
                TransportPolicyTransports.Grpc,
                TransportPolicyEncodings.Protobuf,
                http3Enabled,
                "Control plane gRPC endpoints must remain protobuf/grpc; configure diagnostics.controlPlane.grpcRuntime.enableHttp3 to advertise QUIC support.");
        }
    }

    private static TransportPolicyFinding EvaluateEndpoint(
        TransportPolicyConfiguration configuration,
        TransportPolicyEndpointDescriptor endpoint,
        TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        var categoryRules = ResolveCategoryRules(configuration, endpoint.Category);
        var transportAllowed = categoryRules.AllowedTransports.Count == 0 ||
                               categoryRules.AllowedTransports.Contains(endpoint.Transport, StringComparer.OrdinalIgnoreCase);
        var encodingAllowed = categoryRules.AllowedEncodings.Count == 0 ||
                              categoryRules.AllowedEncodings.Contains(endpoint.Encoding, StringComparer.OrdinalIgnoreCase);
        var prefersTransport = !categoryRules.RequirePreferredTransport ||
                               string.Equals(endpoint.Transport, categoryRules.PreferredTransport, StringComparison.OrdinalIgnoreCase);
        var prefersEncoding = !categoryRules.RequirePreferredEncoding ||
                               string.Equals(endpoint.Encoding, categoryRules.PreferredEncoding, StringComparison.OrdinalIgnoreCase);

        if (transportAllowed && encodingAllowed && prefersTransport && prefersEncoding)
        {
            return new TransportPolicyFinding(
                endpoint.Name,
                endpoint.Category,
                endpoint.Transport,
                endpoint.Encoding,
                endpoint.Http3Enabled,
                TransportPolicyFindingStatus.Compliant,
                "Endpoint is compliant with configured transport policy.",
                null,
                null,
                null,
                endpoint.Hint);
        }

        var exception = FindException(configuration, endpoint, now);
        if (exception is not null)
        {
            var exceptionMessage = BuildExceptionMessage(exception, endpoint);
            return new TransportPolicyFinding(
                endpoint.Name,
                endpoint.Category,
                endpoint.Transport,
                endpoint.Encoding,
                endpoint.Http3Enabled,
                TransportPolicyFindingStatus.Excepted,
                exceptionMessage,
                exception.Name,
                exception.Reason,
                exception.ExpiresAfter,
                endpoint.Hint);
        }

        var details = new List<string>(3);
        if (!transportAllowed)
        {
            var allowed = categoryRules.AllowedTransports.Count == 0
                ? "no transports configured"
                : string.Join(", ", categoryRules.AllowedTransports);
            details.Add($"transport '{endpoint.Transport}' not permitted (allowed: {allowed})");
        }
        else if (!prefersTransport)
        {
            details.Add($"transport '{endpoint.Transport}' downgrades preferred '{categoryRules.PreferredTransport}'");
        }

        if (!encodingAllowed)
        {
            var allowedEncodings = categoryRules.AllowedEncodings.Count == 0
                ? "no encodings configured"
                : string.Join(", ", categoryRules.AllowedEncodings);
            details.Add($"encoding '{endpoint.Encoding}' not permitted (allowed: {allowedEncodings})");
        }
        else if (!prefersEncoding)
        {
            details.Add($"encoding '{endpoint.Encoding}' differs from preferred '{categoryRules.PreferredEncoding}'");
        }

        if (!endpoint.Http3Enabled && string.Equals(endpoint.Transport, TransportPolicyTransports.Http3, StringComparison.OrdinalIgnoreCase))
        {
            details.Add("HTTP/3 was requested but enableHttp3=false in configuration.");
        }

        if (!string.IsNullOrWhiteSpace(endpoint.Hint))
        {
            details.Add(endpoint.Hint!);
        }

        var message = string.Join(" ", details);
        return new TransportPolicyFinding(
            endpoint.Name,
            endpoint.Category,
            endpoint.Transport,
            endpoint.Encoding,
            endpoint.Http3Enabled,
            TransportPolicyFindingStatus.ViolatesPolicy,
            message,
            null,
            null,
            null,
            endpoint.Hint);
    }

    private static TransportPolicyCategoryConfiguration ResolveCategoryRules(
        TransportPolicyConfiguration configuration,
        string category)
    {
        if (configuration.Categories.TryGetValue(category, out var rules) && rules is not null)
        {
            return rules;
        }

        var fallback = new TransportPolicyCategoryConfiguration
        {
            Category = category,
            RequirePreferredEncoding = false,
            RequirePreferredTransport = false,
            PreferredTransport = TransportPolicyTransports.Http3,
            PreferredEncoding = TransportPolicyEncodings.Protobuf,
            Description = $"No explicit transport policy configured for '{category}'."
        };
        configuration.Categories[category] = fallback;
        return fallback;
    }

    private static TransportPolicyExceptionConfiguration? FindException(
        TransportPolicyConfiguration configuration,
        TransportPolicyEndpointDescriptor endpoint,
        DateTimeOffset now)
    {
        foreach (var exception in configuration.Exceptions)
        {
            var category = string.IsNullOrWhiteSpace(exception.Category)
                ? TransportPolicyCategories.Diagnostics
                : exception.Category;

            if (!string.Equals(category, endpoint.Category, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (exception.AppliesTo.Count > 0 &&
                !exception.AppliesTo.Contains(endpoint.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (exception.Transports.Count > 0 &&
                !exception.Transports.Contains(endpoint.Transport, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (exception.Encodings.Count > 0 &&
                !exception.Encodings.Contains(endpoint.Encoding, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (exception.ExpiresAfter is { } expires && expires < now)
            {
                continue;
            }

            return exception;
        }

        return null;
    }

    private static string BuildExceptionMessage(
        TransportPolicyExceptionConfiguration exception,
        TransportPolicyEndpointDescriptor endpoint)
    {
        var parts = new List<string>
        {
            $"Exception '{exception.Name ?? "unnamed"}' allows {endpoint.Transport}/{endpoint.Encoding}"
        };

        if (!string.IsNullOrWhiteSpace(exception.Reason))
        {
            parts.Add($"reason: {exception.Reason}");
        }

        if (exception.ExpiresAfter is { } expires)
        {
            parts.Add($"expires {expires:O}");
        }

        if (!string.IsNullOrWhiteSpace(exception.ApprovedBy))
        {
            parts.Add($"approved by {exception.ApprovedBy}");
        }

        return string.Join(", ", parts);
    }
}

internal sealed record TransportPolicyEvaluationResult(IReadOnlyList<TransportPolicyFinding> Findings)
{
    public bool HasViolations => Findings.Any(static finding => finding.Status == TransportPolicyFindingStatus.ViolatesPolicy);

    public bool HasExceptions => Findings.Any(static finding => finding.Status == TransportPolicyFindingStatus.Excepted);

    public TransportPolicyEvaluationSummary Summary => TransportPolicyEvaluationSummary.From(Findings);
}

internal enum TransportPolicyFindingStatus
{
    Compliant,
    Excepted,
    ViolatesPolicy
}

internal sealed record TransportPolicyFinding(
    string Endpoint,
    string Category,
    string Transport,
    string Encoding,
    bool Http3Enabled,
    TransportPolicyFindingStatus Status,
    string Message,
    string? ExceptionName,
    string? ExceptionReason,
    DateTimeOffset? ExceptionExpiresAfter,
    string? Hint);

internal sealed record TransportPolicyEndpointDescriptor(
    string Name,
    string Category,
    string Transport,
    string Encoding,
    bool Http3Enabled,
    string? Hint);

internal sealed record TransportPolicyEvaluationSummary(int Total, int Compliant, int Excepted, int Violations)
{
    public static TransportPolicyEvaluationSummary From(IReadOnlyList<TransportPolicyFinding> findings)
    {
        if (findings is null || findings.Count == 0)
        {
            return new TransportPolicyEvaluationSummary(0, 0, 0, 0);
        }

        var compliant = findings.Count(static finding => finding.Status == TransportPolicyFindingStatus.Compliant);
        var excepted = findings.Count(static finding => finding.Status == TransportPolicyFindingStatus.Excepted);
        var violations = findings.Count(static finding => finding.Status == TransportPolicyFindingStatus.ViolatesPolicy);
        return new TransportPolicyEvaluationSummary(findings.Count, compliant, excepted, violations);
    }
}
