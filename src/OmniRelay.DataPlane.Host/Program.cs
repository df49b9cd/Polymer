using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
