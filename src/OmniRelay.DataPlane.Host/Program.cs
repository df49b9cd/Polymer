using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using OmniRelay.DataPlane.Host;
using OmniRelay.Plugins.Internal.Observability;
using OmniRelay.Plugins.Internal.Transport;
using OmniRelay.Transport.Host;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
});

// Register the data-plane host services.
builder.Services.AddDataPlaneHost();

// Register built-in transport plugins (HTTP/3 + gRPC) via plugin package for swap-ready architecture.
var transportConfig = builder.Configuration.GetSection("Transports").Get<TransportHostConfig>() ?? new TransportHostConfig();
builder.Services.AddInternalTransportPlugins(options =>
{
    options.HttpUrls.AddRange(transportConfig.HttpUrls);
    options.GrpcUrls.AddRange(transportConfig.GrpcUrls);
    options.HttpRuntime = transportConfig.HttpRuntime;
    options.HttpTls = transportConfig.HttpTls;
    options.GrpcRuntime = transportConfig.GrpcRuntime;
    options.GrpcTls = transportConfig.GrpcTls;
});

// Register observability defaults via plugin (Prometheus + tracing enabled by default).
builder.Services.AddInternalObservabilityPlugins();

var app = builder.Build();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("dataplane-host");

lifetime.ApplicationStarted.Register(() => Logger.Started(logger));
lifetime.ApplicationStopping.Register(() => Logger.Stopping(logger));

await app.RunAsync().ConfigureAwait(false);

static partial class Logger
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "omnirelay dataplane host started")]
    public static partial void Started(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "omnirelay dataplane host stopping")]
    public static partial void Stopping(ILogger logger);
}
