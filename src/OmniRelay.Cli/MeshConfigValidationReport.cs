using OmniRelay.Dispatcher.Config;

namespace OmniRelay.Cli;

internal sealed record MeshConfigValidationReport(
    string Section,
    bool HasViolations,
    bool HasExceptions,
    MeshConfigValidationSummary Summary,
    MeshConfigValidationFinding[] Findings)
{
    public static MeshConfigValidationReport From(string section, TransportPolicyEvaluationResult evaluation)
    {
        var summary = evaluation.Summary;
        var violationRatio = summary.Total == 0
            ? 0d
            : summary.Violations / (double)summary.Total;
        var meshSummary = new MeshConfigValidationSummary(
            summary.Total,
            summary.Compliant,
            summary.Excepted,
            summary.Violations,
            violationRatio);

        var findings = evaluation.Findings
            .Select(finding => new MeshConfigValidationFinding(
                finding.Endpoint,
                finding.Category.ToString(),
                finding.Transport.ToString(),
                finding.Encoding.ToString(),
                finding.Http3Enabled,
                finding.Status.ToString().ToLowerInvariant(),
                finding.Message,
                finding.ExceptionName,
                finding.ExceptionReason,
                finding.ExceptionExpiresAfter,
                finding.Hint))
            .ToArray();

        return new MeshConfigValidationReport(section, evaluation.HasViolations, evaluation.HasExceptions, meshSummary, findings);
    }
}

internal sealed record MeshConfigValidationFinding(
    string Endpoint,
    string Category,
    string Transport,
    string Encoding,
    bool Http3Enabled,
    string Status,
    string Message,
    string? ExceptionName,
    string? ExceptionReason,
    DateTimeOffset? ExceptionExpiresAfter,
    string? Hint);

internal sealed record MeshConfigValidationSummary(
    int Total,
    int Compliant,
    int Excepted,
    int Violations,
    double ViolationRatio);
