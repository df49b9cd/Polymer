using System.Collections.Immutable;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Diagnostics;

/// <summary>Endpoint helpers for OmniRelay diagnostics control-plane routes.</summary>
public static class DiagnosticsEndpointExtensions
{
    public static IEndpointRouteBuilder MapOmniRelayDiagnosticsControlPlane(
        this IEndpointRouteBuilder builder,
        Action<DiagnosticsEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new DiagnosticsEndpointOptions();
        configure?.Invoke(options);

        if (options.EnableLoggingToggle)
        {
            builder.MapGet("/omnirelay/control/logging", (IDiagnosticsRuntime runtime) =>
            {
                var level = runtime.MinimumLogLevel?.ToString();
                return TypedResults.Json(
                    new LoggingStateResponse(level),
                    DiagnosticsJsonContext.Default.LoggingStateResponse);
            });

            builder.MapPost("/omnirelay/control/logging", (DiagnosticsLogLevelRequest request, IDiagnosticsRuntime runtime) =>
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
            });
        }

        if (options.EnableTraceSamplingToggle)
        {
            builder.MapGet("/omnirelay/control/tracing", (IDiagnosticsRuntime runtime) =>
            {
                return TypedResults.Json(
                    new TraceSamplingResponse(runtime.TraceSamplingProbability),
                    DiagnosticsJsonContext.Default.TraceSamplingResponse);
            });

            builder.MapPost("/omnirelay/control/tracing", (DiagnosticsSamplingRequest request, IDiagnosticsRuntime runtime) =>
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
            });
        }

        if (options.EnableLeaseHealthDiagnostics)
        {
            builder.MapGet("/omnirelay/control/lease-health", (IEnumerable<IPeerHealthSnapshotProvider> providers) =>
            {
                var builder = ImmutableArray.CreateBuilder<PeerLeaseHealthSnapshot>();
                foreach (var provider in providers)
                {
                    if (provider is null)
                    {
                        continue;
                    }

                    var snapshot = provider.Snapshot();
                    if (!snapshot.IsDefaultOrEmpty)
                    {
                        builder.AddRange(snapshot);
                    }
                }

                var diagnostics = PeerLeaseHealthDiagnostics.FromSnapshots(builder.ToImmutable());
                return TypedResults.Json(diagnostics, DiagnosticsJsonContext.Default.PeerLeaseHealthDiagnostics);
            });
        }

        if (options.EnablePeerDiagnostics)
        {
            builder.MapGet("/omnirelay/control/peers", MapPeerDiagnostics);
            builder.MapGet("/control/peers", MapPeerDiagnostics);
        }

        return builder;
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
