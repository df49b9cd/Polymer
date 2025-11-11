using System.Diagnostics.CodeAnalysis;
using Hugo;
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
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using static Hugo.Go;
using OmniRelayDispatcher = OmniRelay.Dispatcher.Dispatcher;

namespace OmniRelay.Samples.ConfigToProd;

public sealed class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Configuration
            .SetBasePath(builder.Environment.ContentRootPath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables(prefix: "CONFIG2PROD_")
            .AddCommandLine(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        var hostUrl = builder.Configuration.GetValue<string>("hosting:urls") ?? "http://0.0.0.0:6050";
        builder.WebHost.UseUrls(hostUrl);

        builder.Services.Configure<DiagnosticsControlOptions>(builder.Configuration.GetSection("diagnostics"));
        builder.Services.Configure<ProbeOptions>(builder.Configuration.GetSection("probes"));

        builder.Services.AddSingleton<ProbeState>();
        builder.Services.AddSingleton<DiagnosticsToggleWatcher>();
        builder.Services.AddSingleton<OpsHandlers>();
        builder.Services.AddHealthChecks()
            .AddCheck<DispatcherHealthCheck>("dispatcher");

        builder.Services.AddOmniRelayDispatcher(builder.Configuration.GetSection("omnirelay"));
        builder.Services.AddHostedService(provider => provider.GetRequiredService<DiagnosticsToggleWatcher>());
        builder.Services.AddHostedService<ConfigTemplateRegistrationService>();

        var app = builder.Build();

        app.MapHealthChecks("/healthz", new HealthCheckOptions
        {
            AllowCachingResponses = false,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            }
        });

        app.MapGet("/readyz", (ProbeState probeState) =>
        {
            return probeState.IsReady
                ? TypedResults.Json(
                    new ReadyStatusPayload(
                        Status: "ready",
                        Since: probeState.ReadySinceUtc,
                        Diagnostics: probeState.DiagnosticsSatisfied),
                    ConfigToProdJsonContext.Default.ReadyStatusPayload)
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        });

        app.MapGet("/", (IConfiguration config, IHostEnvironment env) => TypedResults.Json(
            new RootStatusPayload(
                Service: config.GetValue<string>("omnirelay:service"),
                Environment: env.EnvironmentName,
                Message: "Config-to-Prod template running"),
            ConfigToProdJsonContext.Default.RootStatusPayload));

        await app.RunAsync().ConfigureAwait(false);
    }
}

internal sealed class ConfigTemplateRegistrationService(
    OmniRelayDispatcher dispatcher,
    OpsHandlers handlers,
    ProbeState probeState,
    ILogger<ConfigTemplateRegistrationService> logger,
    IOptions<ProbeOptions> probeOptions)
    : IHostedService
{
    private Task? _warmupTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        handlers.RegisterProcedures(dispatcher);
        logger.LogInformation("Registered ops procedures for {Service} using configuration-driven bootstrap.", dispatcher.ServiceName);

        var warmup = probeOptions.Value.ReadyAfter;
        _warmupTask = Task.Run(async () =>
        {
            if (warmup > TimeSpan.Zero)
            {
                await Task.Delay(warmup, cancellationToken).ConfigureAwait(false);
            }

            probeState.MarkWarm();
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        probeState.MarkNotReady();
        if (_warmupTask is not null)
        {
            try
            {
                await _warmupTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when host shuts down.
            }
        }
    }
}

internal sealed class OpsHandlers(ILogger<OpsHandlers> logger)
{
    public void RegisterProcedures(OmniRelayDispatcher dispatcher)
    {
        dispatcher.RegisterJsonUnary<OpsPingRequest, OpsPingResponse>(
            "ops::ping",
            async (context, request) =>
            {
                logger.LogInformation("Responding to ops::ping for caller {Caller}", context.RequestMeta.Caller ?? "unknown");
                await Task.CompletedTask.ConfigureAwait(false);
                return Response<OpsPingResponse>.Create(new OpsPingResponse(
                    $"pong from {context.Dispatcher.ServiceName}",
                    DateTimeOffset.UtcNow,
                    context.RequestMeta.Transport ?? "unknown"));
            },
            configureProcedure: builder => builder.AddAliases(["ops::health"]));

        dispatcher.RegisterOneway(
            "ops::heartbeat",
            builder =>
            {
                builder.Handle((request, _) =>
                {
                    logger.LogInformation("Heartbeat received from {Caller} over {Transport}", request.Meta.Caller ?? "unknown", request.Meta.Transport ?? "unknown");
                    return ValueTask.FromResult<Result<OnewayAck>>(Ok(OnewayAck.Ack()));
                });
            });
    }
}

internal sealed record OpsPingRequest(string Message)
{
    public string Message
    {
        get => field;
        init => field = value;
    } = Message;
}

internal sealed record OpsPingResponse(string Message, DateTimeOffset IssuedAt, string Transport)
{
    public string Message
    {
        get => field;
        init => field = value;
    } = Message;

    public DateTimeOffset IssuedAt
    {
        get => field;
        init => field = value;
    } = IssuedAt;

    public string Transport
    {
        get => field;
        init => field = value;
    } = Transport;
}

internal sealed class DispatcherHealthCheck(OmniRelayDispatcher dispatcher) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return dispatcher.Status == DispatcherStatus.Running
            ? Task.FromResult(HealthCheckResult.Healthy("Dispatcher is running."))
            : Task.FromResult(HealthCheckResult.Unhealthy($"Dispatcher status: {dispatcher.Status}."));
    }
}

internal sealed class DiagnosticsToggleWatcher(
    IOptionsMonitor<DiagnosticsControlOptions> optionsMonitor,
    ProbeState probeState,
    ILogger<DiagnosticsToggleWatcher> logger)
    : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply(optionsMonitor.CurrentValue);
        _subscription = optionsMonitor.OnChange(Apply);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private void Apply(DiagnosticsControlOptions options)
    {
        probeState.SetDiagnostics(options.RuntimeMetricsEnabled);
        logger.LogInformation("Diagnostics toggles updated. Runtime metrics enabled: {Enabled}", options.RuntimeMetricsEnabled);
    }
}

internal sealed class ProbeState(IOptions<ProbeOptions> options, ILogger<ProbeState> logger)
{
    private readonly ProbeOptions _options = options.Value;

    public bool DiagnosticsSatisfied
    {
        get => field;
        private set => field = value;
    }

    public bool WarmupComplete
    {
        get => field;
        private set => field = value;
    }

    public DateTimeOffset? ReadySinceUtc
    {
        get => field;
        private set => field = value;
    }

    public bool IsReady => WarmupComplete && (!_options.RequireDiagnosticsToggle || DiagnosticsSatisfied);

    public void MarkWarm()
    {
        WarmupComplete = true;
        ReadySinceUtc = DateTimeOffset.UtcNow;
        logger.LogInformation("Probe warm-up complete. Ready state satisfied.");
    }

    public void MarkNotReady()
    {
        WarmupComplete = false;
        ReadySinceUtc = null;
        logger.LogInformation("Probe state reset to not ready.");
    }

    public void SetDiagnostics(bool enabled)
    {
        DiagnosticsSatisfied = ! _options.RequireDiagnosticsToggle || enabled;
        logger.LogInformation("Diagnostics requirement {Requirement} with toggle={Toggle}.", _options.RequireDiagnosticsToggle ? "enabled" : "disabled", enabled);
    }
}

internal sealed record DiagnosticsControlOptions
{
    public bool RuntimeMetricsEnabled
    {
        get => field;
        init => field = value;
    }
}

internal sealed record ProbeOptions
{
    public TimeSpan ReadyAfter
    {
        get => field;
        init => field = value;
    } = TimeSpan.FromSeconds(2);

    public bool RequireDiagnosticsToggle
    {
        get => field;
        init => field = value;
    }
}

internal sealed record ReadyStatusPayload(string Status, DateTimeOffset? Since, bool Diagnostics);

internal sealed record RootStatusPayload(string? Service, string? Environment, string Message);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Serialization,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReadyStatusPayload))]
[JsonSerializable(typeof(RootStatusPayload))]
internal partial class ConfigToProdJsonContext : JsonSerializerContext;
