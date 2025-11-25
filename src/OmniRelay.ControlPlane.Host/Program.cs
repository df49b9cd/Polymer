using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OmniRelay.ControlPlane.ControlProtocol;
using OmniRelay.Core.Leadership;

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

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Parse(listenHost), listenPort, listen =>
    {
        listen.Protocols = HttpProtocols.Http2;
    });
});

builder.Services.AddControlProtocol();
builder.Services.AddLeadershipCoordinator();
builder.Services.AddGrpc();

var app = builder.Build();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("controlplane-host");

lifetime.ApplicationStarted.Register(() => HostLog.Started(logger, listenHost, listenPort));
lifetime.ApplicationStopping.Register(() => HostLog.Stopping(logger));

app.MapGrpcService<ControlPlaneWatchService>();
app.MapGrpcService<LeadershipControlGrpcService>();
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
