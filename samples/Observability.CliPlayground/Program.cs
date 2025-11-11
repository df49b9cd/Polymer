using System.Diagnostics.Metrics;
using System.Net.Http;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniRelay.Configuration;
using OmniRelay.Dispatcher;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
#pragma warning disable CA2007

namespace OmniRelay.Samples.ObservabilityCli;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Configuration
            .SetBasePath(builder.Environment.ContentRootPath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
            .AddEnvironmentVariables("OBS_CLI_")
            .AddCommandLine(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.Configure<PlaygroundOptions>(builder.Configuration.GetSection("playground"));
        builder.Services.AddSingleton(provider => new PlaygroundState
        {
            ServiceName = builder.Configuration.GetValue<string>("omnirelay:service") ?? "observability-cli-playground"
        });
        builder.Services.AddHostedService<SampleInvocationService>();
        builder.Services.AddOmniRelayDispatcher(builder.Configuration.GetSection("omnirelay"));
        builder.Services.AddHttpClient("omnirelay-cli", client => client.Timeout = TimeSpan.FromSeconds(5));

        builder.Services.AddHealthChecks()
            .AddCheck<DispatcherHealth>("dispatcher")
            .AddCheck<ProbeHealth>("probe");

        ConfigureOpenTelemetry(builder);

        var app = builder.Build();

        app.MapGet("/", (PlaygroundState state) => TypedResults.Json(
            new PlaygroundSummary(state.ServiceName, state.LastScriptStatus),
            ObservabilityCliJsonContext.Default.PlaygroundSummary));

        app.MapHealthChecks("/healthz", new HealthCheckOptions
        {
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            }
        });

        app.MapGet("/readyz", (PlaygroundState state) =>
        {
            return state.Ready
                ? TypedResults.Json(
                    new ReadyStatus(state.ReadySince),
                    ObservabilityCliJsonContext.Default.ReadyStatus)
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        });

        app.MapPrometheusScrapingEndpoint("/metrics");

        await app.RunAsync().ConfigureAwait(false);
    }

    private static void ConfigureOpenTelemetry(WebApplicationBuilder builder)
    {
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(serviceName: builder.Configuration.GetValue<string>("omnirelay:service") ?? "observability-cli-playground")
            .AddAttributes(new KeyValuePair<string, object>[]
            {
                new("deployment.environment", builder.Environment.EnvironmentName ?? "Unknown")
            });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.SetResourceBuilder(resourceBuilder)
                    .AddRuntimeInstrumentation()
                    .AddMeter("OmniRelay.Samples.ObservabilityCli")
                    .AddPrometheusExporter();
            })
            .WithTracing(tracing =>
            {
                tracing.SetResourceBuilder(resourceBuilder)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddConsoleExporter();
            });
    }
}

internal sealed class SampleInvocationService(
    IHttpClientFactory httpClientFactory,
    ILogger<SampleInvocationService> logger,
    IOptions<PlaygroundOptions> options,
    PlaygroundState state) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(options.Value.InitialDelay, stoppingToken).ConfigureAwait(false);
        state.MarkReady();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = httpClientFactory.CreateClient("omnirelay-cli");
                var response = await client.GetAsync(options.Value.IntrospectUrl, stoppingToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var payload = await response.Content.ReadAsByteArrayAsync(stoppingToken).ConfigureAwait(false);
                state.LastScriptStatus = $"introspect-ok bytes={payload.Length}";
                logger.LogInformation("Introspection script succeeded ({Bytes} bytes).", payload.Length);
            }
            catch (Exception ex)
            {
                state.LastScriptStatus = $"introspect-failed: {ex.Message}";
                logger.LogWarning(ex, "Introspection script failed");
            }

            await Task.Delay(options.Value.ScriptInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}

internal sealed class DispatcherHealth(OmniRelay.Dispatcher.Dispatcher dispatcher) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return dispatcher.Status == DispatcherStatus.Running
            ? Task.FromResult(HealthCheckResult.Healthy("dispatcher running"))
            : Task.FromResult(HealthCheckResult.Unhealthy($"dispatcher status {dispatcher.Status}"));
    }
}

internal sealed class ProbeHealth(PlaygroundState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return state.Ready
            ? Task.FromResult(HealthCheckResult.Healthy("ready"))
            : Task.FromResult(HealthCheckResult.Degraded("waiting for scripts"));
    }
}

internal sealed record PlaygroundOptions
{
    public TimeSpan InitialDelay
    {
        get => field;
        init => field = value;
    } = TimeSpan.FromSeconds(3);

    public TimeSpan ScriptInterval
    {
        get => field;
        init => field = value;
    } = TimeSpan.FromSeconds(15);

    public string IntrospectUrl
    {
        get => field;
        init => field = value;
    } = "http://127.0.0.1:7130/omnirelay/introspect";
}

internal sealed class PlaygroundState
{
    public string ServiceName
    {
        get => field;
        set => field = value;
    } = "observability-cli-playground";

    public string? LastScriptStatus
    {
        get => field;
        set => field = value;
    }

    public bool Ready
    {
        get => field;
        private set => field = value;
    }

    public DateTimeOffset? ReadySince
    {
        get => field;
        private set => field = value;
    }

    public void MarkReady()
    {
        if (!Ready)
        {
            Ready = true;
            ReadySince = DateTimeOffset.UtcNow;
        }
    }

    public void Reset()
    {
        Ready = false;
        ReadySince = null;
    }
}

internal sealed record PlaygroundSummary(string ServiceName, string? LastScriptStatus);

internal sealed record ReadyStatus(DateTimeOffset? Since);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Serialization,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PlaygroundSummary))]
[JsonSerializable(typeof(ReadyStatus))]
internal partial class ObservabilityCliJsonContext : JsonSerializerContext;
