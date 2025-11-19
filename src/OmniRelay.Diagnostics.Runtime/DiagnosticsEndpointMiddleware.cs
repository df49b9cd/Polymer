using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Diagnostics;

/// <summary>Diagnostics routing implemented without Minimal APIs for native AOT compatibility.</summary>
public static class DiagnosticsEndpointMiddleware
{
    public static WebApplication UseOmniRelayDiagnosticsControlPlane(
        this WebApplication app,
        Action<DiagnosticsEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = new DiagnosticsEndpointOptions();
        configure?.Invoke(options);

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            var method = context.Request.Method;

            if (options.EnableLoggingToggle && path is "/omnirelay/control/logging")
            {
                if (HttpMethods.IsGet(method))
                {
                    await WriteLoggingStateAsync(context).ConfigureAwait(false);
                    return;
                }

                if (HttpMethods.IsPost(method))
                {
                    await SetLoggingLevelAsync(context).ConfigureAwait(false);
                    return;
                }
            }

            if (options.EnableTraceSamplingToggle && path is "/omnirelay/control/tracing")
            {
                if (HttpMethods.IsGet(method))
                {
                    await WriteTraceSamplingAsync(context).ConfigureAwait(false);
                    return;
                }

                if (HttpMethods.IsPost(method))
                {
                    await SetTraceSamplingAsync(context).ConfigureAwait(false);
                    return;
                }
            }

            if (options.EnableLeaseHealthDiagnostics && path is "/omnirelay/control/lease-health" && HttpMethods.IsGet(method))
            {
                await WriteLeaseHealthAsync(context).ConfigureAwait(false);
                return;
            }

            if (options.EnablePeerDiagnostics && (path is "/omnirelay/control/peers" or "/control/peers") && HttpMethods.IsGet(method))
            {
                await WritePeerDiagnosticsAsync(context).ConfigureAwait(false);
                return;
            }

            await next().ConfigureAwait(false);
        });

        return app;
    }

    private static async Task WriteLoggingStateAsync(HttpContext context)
    {
        var runtime = context.RequestServices.GetRequiredService<IDiagnosticsRuntime>();
        var payload = new LoggingStateResponse(runtime.MinimumLogLevel?.ToString());
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                payload,
                DiagnosticsJsonContext.Default.LoggingStateResponse,
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static async Task SetLoggingLevelAsync(HttpContext context)
    {
        var runtime = context.RequestServices.GetRequiredService<IDiagnosticsRuntime>();
        var request = await context.Request.ReadFromJsonAsync(
                DiagnosticsJsonContext.Default.DiagnosticsLogLevelRequest,
                context.RequestAborted)
            .ConfigureAwait(false);

        if (request is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("{\"error\":\"Request body required.\"}", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Level))
        {
            runtime.SetMinimumLogLevel(null);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (!Enum.TryParse<LogLevel>(request.Level, ignoreCase: true, out var parsed))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync($"{{\"error\":\"Invalid log level '{request.Level}'.\"}}", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        runtime.SetMinimumLogLevel(parsed);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task WriteTraceSamplingAsync(HttpContext context)
    {
        var runtime = context.RequestServices.GetRequiredService<IDiagnosticsRuntime>();
        var payload = new TraceSamplingResponse(runtime.TraceSamplingProbability);
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                payload,
                DiagnosticsJsonContext.Default.TraceSamplingResponse,
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static async Task SetTraceSamplingAsync(HttpContext context)
    {
        var runtime = context.RequestServices.GetRequiredService<IDiagnosticsRuntime>();
        var request = await context.Request.ReadFromJsonAsync(
                DiagnosticsJsonContext.Default.DiagnosticsSamplingRequest,
                context.RequestAborted)
            .ConfigureAwait(false);

        if (request is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("{\"error\":\"Request body required.\"}", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        try
        {
            runtime.SetTraceSamplingProbability(request.Probability);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync($"{{\"error\":\"{ex.Message}\"}}", context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static async Task WriteLeaseHealthAsync(HttpContext context)
    {
        var providers = context.RequestServices.GetRequiredService<IEnumerable<IPeerHealthSnapshotProvider>>();
        var snapshots = ImmutableArray.CreateBuilder<PeerLeaseHealthSnapshot>();
        foreach (var provider in providers)
        {
            var snapshot = provider?.Snapshot() ?? default;
            if (!snapshot.IsDefaultOrEmpty)
            {
                snapshots.AddRange(snapshot);
            }
        }

        var diagnostics = PeerLeaseHealthDiagnostics.FromSnapshots(snapshots.ToImmutable());
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                diagnostics,
                DiagnosticsJsonContext.Default.PeerLeaseHealthDiagnostics,
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static async Task WritePeerDiagnosticsAsync(HttpContext context)
    {
        var provider = context.RequestServices.GetService<IPeerDiagnosticsProvider>();
        if (provider is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("{\"error\":\"Peer diagnostics provider unavailable.\"}", context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        var response = provider.CreateSnapshot();
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                response,
                DiagnosticsJsonContext.Default.PeerDiagnosticsResponse,
                context.RequestAborted)
            .ConfigureAwait(false);
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
