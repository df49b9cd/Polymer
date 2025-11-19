using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Diagnostics;

/// <summary>Endpoint helpers for OmniRelay diagnostics control-plane routes.</summary>
public static class DiagnosticsEndpointExtensions
{
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Diagnostic endpoints run in non-trimmed control plane hosts.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Diagnostic control plane is not published as native AOT.")]
    public static IEndpointRouteBuilder MapOmniRelayDiagnosticsControlPlane(
        this IEndpointRouteBuilder builder,
        Action<DiagnosticsEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new DiagnosticsEndpointOptions();
        configure?.Invoke(options);

        if (options.EnableLoggingToggle)
        {
            builder.MapGet("/omnirelay/control/logging", GetLoggingState);
            builder.MapPost("/omnirelay/control/logging", SetLoggingLevel);
        }

        if (options.EnableTraceSamplingToggle)
        {
            builder.MapGet("/omnirelay/control/tracing", GetTraceSampling);
            builder.MapPost("/omnirelay/control/tracing", SetTraceSampling);
        }

        if (options.EnableLeaseHealthDiagnostics)
        {
            builder.MapGet("/omnirelay/control/lease-health", GetLeaseHealth);
        }

        if (options.EnablePeerDiagnostics)
        {
            builder.MapGet("/omnirelay/control/peers", MapPeerDiagnostics);
            builder.MapGet("/control/peers", MapPeerDiagnostics);
        }

        return builder;
    }

    private static JsonHttpResult<LoggingStateResponse> GetLoggingState(IDiagnosticsRuntime runtime)
    {
        var level = runtime.MinimumLogLevel?.ToString();
        return TypedResults.Json(
            new LoggingStateResponse(level),
            DiagnosticsJsonContext.Default.LoggingStateResponse);
    }

    private static IResult SetLoggingLevel(DiagnosticsLogLevelRequest request, IDiagnosticsRuntime runtime)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "Request body required." });
        }

        if (string.IsNullOrWhiteSpace(request.Level))
        {
            runtime.SetMinimumLogLevel(null);
            return Results.NoContent();
        }

        if (!Enum.TryParse<LogLevel>(request.Level, ignoreCase: true, out var parsed))
        {
            return Results.BadRequest(new { error = $"Invalid log level '{request.Level}'." });
        }

        runtime.SetMinimumLogLevel(parsed);
        return Results.NoContent();
    }

    private static JsonHttpResult<TraceSamplingResponse> GetTraceSampling(IDiagnosticsRuntime runtime)
    {
        return TypedResults.Json(
            new TraceSamplingResponse(runtime.TraceSamplingProbability),
            DiagnosticsJsonContext.Default.TraceSamplingResponse);
    }

    private static IResult SetTraceSampling(DiagnosticsSamplingRequest request, IDiagnosticsRuntime runtime)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "Request body required." });
        }

        try
        {
            runtime.SetTraceSamplingProbability(request.Probability);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        return Results.NoContent();
    }

    private static JsonHttpResult<PeerLeaseHealthDiagnostics> GetLeaseHealth(IEnumerable<IPeerHealthSnapshotProvider> providers)
    {
        var snapshots = ImmutableArray.CreateBuilder<PeerLeaseHealthSnapshot>();
        foreach (var provider in providers)
        {
            if (provider is null)
            {
                continue;
            }

            var snapshot = provider.Snapshot();
            if (!snapshot.IsDefaultOrEmpty)
            {
                snapshots.AddRange(snapshot);
            }
        }

        var diagnostics = PeerLeaseHealthDiagnostics.FromSnapshots(snapshots.ToImmutable());
        return TypedResults.Json(diagnostics, DiagnosticsJsonContext.Default.PeerLeaseHealthDiagnostics);
    }

    private static IResult MapPeerDiagnostics(IPeerDiagnosticsProvider? provider)
    {
        if (provider is null)
        {
            return Results.Problem("Peer diagnostics provider unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var response = provider.CreateSnapshot();
        return TypedResults.Json(response, DiagnosticsJsonContext.Default.PeerDiagnosticsResponse);
    }
}

/// <summary>Options controlling which diagnostics endpoints are mapped.</summary>
public sealed class DiagnosticsEndpointOptions
{
    public bool EnableLoggingToggle { get; set; } = true;

    public bool EnableTraceSamplingToggle { get; set; } = true;

    public bool EnableLeaseHealthDiagnostics { get; set; } = true;

    public bool EnablePeerDiagnostics { get; set; } = true;
}
