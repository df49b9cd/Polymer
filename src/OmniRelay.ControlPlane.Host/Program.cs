using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Hugo;
using OmniRelay.ControlPlane.Bootstrap;
using OmniRelay.ControlPlane.ControlProtocol;
using OmniRelay.Core.Leadership;
using OmniRelay.Diagnostics;
using OmniRelay.Identity;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

var listenHost = builder.Configuration["ControlPlane:Grpc:Host"];
if (string.IsNullOrWhiteSpace(listenHost))
{
    listenHost = "0.0.0.0";
}

var listenPort = 17421;
var portValue = builder.Configuration["ControlPlane:Grpc:Port"];
if (int.TryParse(portValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort) && parsedPort > 0)
{
    listenPort = parsedPort;
}

var httpHost = builder.Configuration["ControlPlane:Http:Host"];
if (string.IsNullOrWhiteSpace(httpHost))
{
    httpHost = "0.0.0.0";
}

var httpPort = 8080;
var httpPortValue = builder.Configuration["ControlPlane:Http:Port"];
if (int.TryParse(httpPortValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHttpPort) && parsedHttpPort > 0)
{
    httpPort = parsedHttpPort;
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Parse(listenHost), listenPort, listen =>
    {
        listen.Protocols = HttpProtocols.Http2;
    });

    options.Listen(IPAddress.Parse(httpHost), httpPort, listen =>
    {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
    });
});

builder.Services.AddControlProtocol();
builder.Services.AddLeadershipCoordinator();
builder.Services.AddCertificateAuthority(builder.Configuration.GetSection("CertificateAuthority"));
builder.Services.AddGrpc();
builder.Services.AddOmniRelayDiagnosticsRuntime();

ControlPlaneHostHelpers.ConfigureBootstrap(builder.Services, builder.Configuration);

var app = builder.Build();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("controlplane-host");

lifetime.ApplicationStarted.Register(() => HostLog.Started(logger, listenHost, listenPort));
lifetime.ApplicationStopping.Register(() => HostLog.Stopping(logger));

app.MapGrpcService<ControlPlaneWatchService>();
app.MapGrpcService<LeadershipControlGrpcService>();

app.UseOmniRelayDiagnosticsControlPlane(options =>
{
    options.EnableLoggingToggle = true;
    options.EnableTraceSamplingToggle = true;
    options.EnableLeaseHealthDiagnostics = true;
    options.EnablePeerDiagnostics = true;
});

if (app.Services.GetService<BootstrapServer>() is not null)
{
    app.MapPost("/omnirelay/bootstrap/join", ControlPlaneHostHelpers.HandleBootstrapJoinAsync);
}

app.Map("/healthz", static context =>
{
    context.Response.StatusCode = StatusCodes.Status200OK;
    return context.Response.CompleteAsync();
});

await app.RunAsync().ConfigureAwait(false);

internal static partial class HostLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "omnirelay control-plane host listening on {Host}:{Port} (h2)")]
    public static partial void Started(ILogger logger, string host, int port);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "omnirelay control-plane host stopping")]
    public static partial void Stopping(ILogger logger);
}

internal static class ControlPlaneHostHelpers
{
    public static void ConfigureBootstrap(IServiceCollection services, IConfiguration configuration)
    {
        var signingKeyBase64 = configuration["Bootstrap:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKeyBase64))
        {
            return;
        }

        byte[] signingKey;
        try
        {
            signingKey = Convert.FromBase64String(signingKeyBase64);
        }
        catch (FormatException)
        {
            return;
        }

        var certificatePath = configuration["Bootstrap:CertificatePath"];
        if (string.IsNullOrWhiteSpace(certificatePath) || !File.Exists(certificatePath))
        {
            return;
        }

        var certificatePassword = configuration["Bootstrap:CertificatePassword"];
        var trustBundlePath = configuration["Bootstrap:TrustBundlePath"];
        var trustBundle = !string.IsNullOrWhiteSpace(trustBundlePath) && File.Exists(trustBundlePath)
            ? File.ReadAllText(trustBundlePath)
            : null;

        var certificateData = File.ReadAllBytes(certificatePath);

        var tokenOptions = new BootstrapTokenSigningOptions
        {
            SigningKey = signingKey,
            Issuer = configuration["Bootstrap:Issuer"] ?? "omnirelay-bootstrap",
            DefaultLifetime = ParseTimeSpan(configuration["Bootstrap:TokenLifetime"], TimeSpan.FromHours(1)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        var serverOptions = new BootstrapServerOptions
        {
            ClusterId = configuration["Bootstrap:ClusterId"] ?? "default",
            DefaultRole = configuration["Bootstrap:DefaultRole"] ?? "worker",
            BundlePassword = configuration["Bootstrap:BundlePassword"],
            JoinTimeout = ParseTimeSpan(configuration["Bootstrap:JoinTimeout"], TimeSpan.FromSeconds(15))
        };

        foreach (var seed in configuration.GetSection("Bootstrap:SeedPeers").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(seed.Value))
            {
                serverOptions.SeedPeers.Add(seed.Value.Trim());
            }
        }

        services.AddSingleton(tokenOptions);
        services.AddSingleton(serverOptions);
        services.AddSingleton<IBootstrapReplayProtector, InMemoryBootstrapReplayProtector>();
        services.AddSingleton(sp =>
        {
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new FileBootstrapIdentityProvider(certificateData, certificatePassword, trustBundle, timeProvider);
        });
        services.AddSingleton<BootstrapTokenService>();
        services.AddSingleton<BootstrapPolicyEvaluator>();
        services.AddSingleton<BootstrapServer>();
    }

    public static async Task HandleBootstrapJoinAsync(HttpContext context)
    {
        var request = await context.Request.ReadFromJsonAsync(
            BootstrapJsonContext.Default.BootstrapJoinRequest,
            context.RequestAborted).ConfigureAwait(false);

        if (request is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("{\"error\":\"Request body required.\"}", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var server = context.RequestServices.GetRequiredService<BootstrapServer>();
        var token = context.RequestAborted;
        var result = await server.JoinAsync(request, token).ConfigureAwait(false);
        if (result.TryGetValue(out var payload))
        {
            await Results.Ok(payload).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        _ = result.TryGetError(out var error);
        await MapBootstrapError(error ?? Error.From("Unknown bootstrap failure."))
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }

    private static IResult MapBootstrapError(Error error)
    {
        var statusCode = error.Code switch
        {
            ErrorCodes.Validation => StatusCodes.Status400BadRequest,
            ErrorCodes.Canceled => StatusCodes.Status499ClientClosedRequest,
            ErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status500InternalServerError
        };

        var response = new BootstrapErrorResponse(error.Code, error.Message);
        return Results.Json(response, BootstrapJsonContext.Default.BootstrapErrorResponse, statusCode: statusCode);
    }

    private static TimeSpan ParseTimeSpan(string? value, TimeSpan fallback) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed > TimeSpan.Zero
            ? parsed
            : fallback;
}
