using System.Diagnostics.CodeAnalysis;
using Hugo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.ControlPlane.Hosting;
using OmniRelay.ControlPlane.Security;
using OmniRelay.Core.Transport;

namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>Dedicated HTTP host serving bootstrap/join endpoints.</summary>
internal sealed partial class BootstrapControlPlaneHost : ILifecycle, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly HttpControlPlaneHostOptions _options;
    private readonly BootstrapServerOptions _serverOptions;
    private readonly ILogger<BootstrapControlPlaneHost> _logger;
    private readonly TransportTlsManager? _tlsManager;
    private WebApplication? _app;
    private CancellationTokenSource? _cts;

    public BootstrapControlPlaneHost(
        IServiceProvider services,
        HttpControlPlaneHostOptions options,
        BootstrapServerOptions serverOptions,
        ILogger<BootstrapControlPlaneHost> logger,
        TransportTlsManager? tlsManager = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _serverOptions = serverOptions ?? throw new ArgumentNullException(nameof(serverOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tlsManager = tlsManager;
    }

    [UnconditionalSuppressMessage("TrimAnalysis", "IL2026", Justification = "Minimal API endpoint mapping is not part of trimmed publishing scenarios.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Bootstrap control-plane host runs on dynamic runtime, not native AOT.")]
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        if (_options.Urls.Count == 0)
        {
            Log.NoBootstrapUrlsConfigured(_logger);
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var builder = new HttpControlPlaneHostBuilder(_options);

        var tokenService = _services.GetRequiredService<BootstrapTokenService>();
        var identityProvider = _services.GetRequiredService<IWorkloadIdentityProvider>();
        var policyEvaluator = _services.GetRequiredService<BootstrapPolicyEvaluator>();
        var loggerFactory = _services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        var bootstrapServer = new BootstrapServer(
            _serverOptions,
            tokenService,
            identityProvider,
            policyEvaluator,
            loggerFactory.CreateLogger<BootstrapServer>());

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(bootstrapServer);
        });

        builder.ConfigureApp(app =>
        {
            app.MapPost("/omnirelay/bootstrap/join", async (BootstrapJoinRequest request, BootstrapServer server, CancellationToken token) =>
            {
                var result = await server.JoinAsync(request, token).ConfigureAwait(false);
                if (result.TryGetValue(out var payload))
                {
                    return Results.Ok(payload);
                }

                _ = result.TryGetError(out var error);
                return MapJoinError(error ?? Error.From("Unknown bootstrap failure."));
            });
        });

        var app = builder.Build();
        await app.StartAsync(_cts.Token).ConfigureAwait(false);
        _app = app;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        using var cts = _cts;
        _cts = null;

        try
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _app = null;
        }
    }

    public void Dispose()
    {
        _tlsManager?.Dispose();
    }

    private static IResult MapJoinError(Error error)
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

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Bootstrap host did not start because no URLs were configured.")]
        public static partial void NoBootstrapUrlsConfigured(ILogger logger);
    }
}
