using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OmniRelay.Configuration.Internal;
using OmniRelay.Configuration.Internal.Security;
using OmniRelay.Configuration.Models;
using OmniRelay.ControlPlane.Bootstrap;
using OmniRelay.ControlPlane.Security;
using OmniRelay.ControlPlane.Upgrade;
using OmniRelay.Core.Diagnostics;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Leadership;
using OmniRelay.Core.Peers;
using OmniRelay.Diagnostics;
using OmniRelay.Diagnostics.Alerting;
using OmniRelay.Security.Authorization;
using OmniRelay.Security.Secrets;
using OmniRelay.Transport.Security;

namespace OmniRelay.Configuration;

/// <summary>
/// Dependency injection extensions to configure and host an OmniRelay dispatcher from IConfiguration.
/// </summary>
public static class OmniRelayServiceCollectionExtensions
{
    private const string AotWarning = "OmniRelay dispatcher bootstrapping uses reflection and dynamic configuration; it is not trimming/AOT safe.";

    /// <summary>
    /// Adds and configures an <see cref="Dispatcher.Dispatcher"/> using the provided configuration section.
    /// Binds options, wires diagnostics, builds the dispatcher, and registers a hosted service to manage its lifecycle.
    /// </summary>
    [RequiresDynamicCode(AotWarning)]
    [RequiresUnreferencedCode(AotWarning)]
    public static IServiceCollection AddOmniRelayDispatcher(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentNullException.ThrowIfNull(configuration);

        var snapshot = new OmniRelayConfigurationOptions();
        configuration.Bind(snapshot);
        ValidateBasicConfiguration(snapshot);

        var (minimumLevel, overrides) = ParseLoggingConfiguration(snapshot.Logging);

        services.Configure<OmniRelayConfigurationOptions>(configuration);
        services.TryAddSingleton<NodeDrainCoordinator>();

        var loggingOptions = new OmniRelayLoggingOptions
        {
            MinimumLevel = minimumLevel
        };

        foreach (var (category, level) in overrides)
        {
            loggingOptions.CategoryLevels[category] = level;
        }

        services.AddLogging(builder => builder.AddOmniRelayLogging(loggingOptions));

        var gossipSection = configuration.GetSection("mesh:gossip");
        if (gossipSection.Exists())
        {
            services.AddMeshGossipAgent(gossipSection);
        }
        else
        {
            services.TryAddSingleton<IMeshGossipAgent>(NullMeshGossipAgent.Instance);
        }

        services.TryAddSingleton<IPeerDiagnosticsProvider>(sp =>
        {
            var agent = sp.GetService<IMeshGossipAgent>();
            if (agent is null || ReferenceEquals(agent, NullMeshGossipAgent.Instance))
            {
                return new NullPeerDiagnosticsProvider();
            }

            return new MeshPeerDiagnosticsProvider(agent);
        });

        var leadershipSection = configuration.GetSection("mesh:leadership");
        if (leadershipSection.Exists())
        {
            services.AddLeadershipCoordinator(leadershipSection);
        }

        // Ensure HttpClientFactory is available so named HTTP outbounds can be used if configured.
        services.AddHttpClient();

        ConfigureDiagnostics(services, snapshot);
        ConfigureSecurity(services, snapshot);

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<OmniRelayConfigurationOptions>>().Value;
            var builder = new DispatcherBuilder(options, provider, configuration);
            return builder.Build();
        });

        services.AddSingleton(provider => provider.GetRequiredService<Dispatcher.Dispatcher>().Codecs);

        services.AddSingleton<IHostedService>(provider =>
        {
            var dispatcher = provider.GetRequiredService<Dispatcher.Dispatcher>();
            var logger = provider.GetService<ILogger<DispatcherHostedService>>();
            return new DispatcherHostedService(dispatcher, logger);
        });

        return services;
    }

    private static void ConfigureDiagnostics(IServiceCollection services, OmniRelayConfigurationOptions options)
    {
        var diagnostics = options.Diagnostics;

        ConfigureRuntimeDiagnostics(services, diagnostics);
        ConfigureDocumentationDiagnostics(services, diagnostics);
        ConfigureProbeDiagnostics(services, diagnostics);

        var otel = diagnostics.OpenTelemetry;

        var prometheusEnabled = otel.Prometheus.Enabled ?? true;
        var otlpEnabled = otel.Otlp.Enabled ?? false;
        var metricsEnabled = otel.EnableMetrics ?? (prometheusEnabled || otlpEnabled);

        if (!metricsEnabled)
        {
            prometheusEnabled = false;
            otlpEnabled = false;
        }

        var otelEnabled = otel.Enabled ?? metricsEnabled;
        if (!otelEnabled)
        {
            return;
        }

        var serviceName = string.IsNullOrWhiteSpace(otel.ServiceName) ? options.Service ?? "OmniRelay" : otel.ServiceName!;

        var telemetryOptions = new OmniRelayTelemetryOptions
        {
            ServiceName = serviceName,
            EnableTelemetry = otelEnabled,
            EnableMetrics = metricsEnabled,
            EnableTracing = otelEnabled && (otlpEnabled || (diagnostics.Runtime.EnableTraceSamplingToggle ?? false)),
            EnableRuntimeTraceSampler = diagnostics.Runtime.EnableTraceSamplingToggle ?? false,
        };

        telemetryOptions.Prometheus.Enabled = prometheusEnabled;
        telemetryOptions.Prometheus.ScrapeEndpointPath = otel.Prometheus.ScrapeEndpointPath;
        telemetryOptions.Otlp.Enabled = otlpEnabled;
        telemetryOptions.Otlp.Endpoint = otel.Otlp.Endpoint;
        telemetryOptions.Otlp.Protocol = otel.Otlp.Protocol;

        services.AddOmniRelayTelemetry(telemetryOptions);

        // Bridge QUIC/Kestrel events to the logging pipeline for structured observability
        services.AddHostedService<QuicDiagnosticsHostedService>();
    }

    private static void ConfigureRuntimeDiagnostics(
        IServiceCollection services,
        DiagnosticsConfiguration diagnostics)
    {
        var runtime = diagnostics.Runtime;

        var enableLoggingToggle = runtime.EnableLoggingLevelToggle ?? false;
        var enableSamplingToggle = runtime.EnableTraceSamplingToggle ?? false;
        var enableControlPlane = runtime.EnableControlPlane ?? (enableLoggingToggle || enableSamplingToggle);

        if (!enableControlPlane && !enableLoggingToggle && !enableSamplingToggle)
        {
            return;
        }

        services.AddOmniRelayDiagnosticsRuntime();
    }

    private static void ConfigureDocumentationDiagnostics(
        IServiceCollection services,
        DiagnosticsConfiguration diagnostics)
    {
        var documentation = diagnostics.Documentation ?? new DocumentationDiagnosticsConfiguration();
        var enableOpenApi = documentation.EnableOpenApi ?? false;
        var enableGrpcReflection = documentation.EnableGrpcReflection ?? false;
        if (!enableOpenApi && !enableGrpcReflection)
        {
            return;
        }

        services.AddOmniRelayDocumentation(options =>
        {
            options.EnableOpenApi = enableOpenApi;
            options.EnableGrpcReflection = enableGrpcReflection;
            if (!string.IsNullOrWhiteSpace(documentation.Route))
            {
                options.RoutePattern = documentation.Route!;
            }

            options.AuthorizationPolicy = documentation.AuthorizationPolicy;
            foreach (var pair in documentation.Metadata)
            {
                options.Metadata[pair.Key] = pair.Value;
            }
        });
    }

    private static void ConfigureProbeDiagnostics(
        IServiceCollection services,
        DiagnosticsConfiguration diagnostics)
    {
        var probes = diagnostics.Probes ?? new ProbesDiagnosticsConfiguration();
        var chaos = diagnostics.Chaos ?? new ChaosDiagnosticsConfiguration();
        var enableScheduler = probes.EnableScheduler ?? false;
        var enableProbeEndpoint = probes.EnableDiagnosticsEndpoint ?? false;
        var enableChaosCoordinator = chaos.EnableCoordinator ?? false;
        var enableChaosControl = chaos.EnableControlEndpoint ?? false;

        if (!(enableScheduler || enableProbeEndpoint || enableChaosCoordinator || enableChaosControl))
        {
            return;
        }

        services.AddOmniRelayProbes(enableScheduler || enableChaosCoordinator);
        services.PostConfigure<ProbeDiagnosticsOptions>(options =>
        {
            options.EnableProbeResults = enableProbeEndpoint;
            options.EnableChaosControl = enableChaosControl;
            options.ProbeAuthorizationPolicy = probes.AuthorizationPolicy;
            options.ChaosAuthorizationPolicy = chaos.AuthorizationPolicy;
        });
    }

    private static void ValidateBasicConfiguration(OmniRelayConfigurationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Service))
        {
            throw new OmniRelayConfigurationException("OmniRelay configuration must specify a service name.");
        }
    }

    private static void ConfigureSecurity(
        IServiceCollection services,
        OmniRelayConfigurationOptions options)
    {
        var security = options.Security ?? new SecurityConfiguration();
        services.TryAddSingleton<ISecretAccessAuditor, LoggingSecretAccessAuditor>();
        services.TryAddSingleton<ISecretProvider>(sp => SecretProviderFactory.Create(security.Secrets, sp));

        var transportPolicy = TransportSecurityFactory.Create(security.Transport);
        if (transportPolicy is not null)
        {
            services.TryAddSingleton(transportPolicy);
            services.TryAddSingleton<TransportSecurityPolicyEvaluator>();
            services.TryAddSingleton<TransportSecurityGrpcInterceptor>();
        }

        var authorizationEvaluator = AuthorizationFactory.Create(security.Authorization, NullLogger<MeshAuthorizationEvaluator>.Instance);
        if (authorizationEvaluator is not null)
        {
            services.TryAddSingleton(authorizationEvaluator);
        }

        if (security.Alerting.Enabled == true)
        {
            services.TryAddSingleton<IAlertPublisher>(sp =>
            {
                var publisher = AlertingFactory.Create(security.Alerting, sp);
                return publisher ?? new NullAlertPublisher();
            });
        }
        else
        {
            services.TryAddSingleton<IAlertPublisher, NullAlertPublisher>();
        }

        services.AddPeerLeaseHealthDiagnostics();

        ConfigureBootstrapServices(services, security.Bootstrap, options.Service);
    }

    private static void ConfigureBootstrapServices(IServiceCollection services, BootstrapConfiguration? configuration, string? serviceName)
    {
        if (configuration?.Enabled != true)
        {
            return;
        }

        services.TryAddSingleton<IBootstrapReplayProtector, InMemoryBootstrapReplayProtector>();
        services.TryAddSingleton(sp =>
        {
            var secretProvider = sp.GetService<ISecretProvider>();
            var signingKey = ResolveBootstrapSigningKey(configuration.Signing, secretProvider);
            var signingOptions = new BootstrapTokenSigningOptions
            {
                SigningKey = signingKey,
                Issuer = configuration.Signing.Issuer ?? serviceName ?? "omnirelay",
                DefaultLifetime = configuration.Signing.DefaultLifetime ?? TimeSpan.FromHours(1),
                DefaultMaxUses = configuration.Signing.MaxUses
            };

            var replay = sp.GetRequiredService<IBootstrapReplayProtector>();
            var logger = sp.GetRequiredService<ILogger<BootstrapTokenService>>();
            var timeProvider = sp.GetService<TimeProvider>();
            return new BootstrapTokenService(signingOptions, replay, logger, timeProvider);
        });

        services.TryAddSingleton(_ => CreateBootstrapServerOptions(configuration, serviceName));
    }

    private static byte[] ResolveBootstrapSigningKey(BootstrapSigningConfiguration signing, ISecretProvider? secretProvider)
    {
        if (!string.IsNullOrWhiteSpace(signing.SigningKey))
        {
            return Encoding.UTF8.GetBytes(signing.SigningKey);
        }

        if (!string.IsNullOrWhiteSpace(signing.SigningKeySecret) && secretProvider is not null)
        {
            using var secret = secretProvider.GetSecretSync(signing.SigningKeySecret)
                ?? throw new OmniRelayConfigurationException($"Bootstrap signing secret '{signing.SigningKeySecret}' was not found.");
            var value = secret.AsString();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new OmniRelayConfigurationException($"Bootstrap signing secret '{signing.SigningKeySecret}' was empty.");
            }

            return Encoding.UTF8.GetBytes(value);
        }

        throw new OmniRelayConfigurationException("Bootstrap signing key (security.bootstrap.signing.signingKey or signingKeySecret) must be configured when bootstrap hosting is enabled.");
    }

    private static BootstrapServerOptions CreateBootstrapServerOptions(BootstrapConfiguration configuration, string? serviceName)
    {
        var certificate = BuildTransportTlsOptions(configuration.Tls);
        var options = new BootstrapServerOptions
        {
            ClusterId = configuration.ClusterId ?? serviceName ?? "omnirelay",
            DefaultRole = configuration.DefaultRole ?? "worker",
            BundlePassword = configuration.BundlePassword,
            Certificate = certificate
        };

        foreach (var seed in configuration.SeedPeers)
        {
            if (string.IsNullOrWhiteSpace(seed))
            {
                continue;
            }

            options.SeedPeers.Add(seed);
        }

        return options;
    }

    private static TransportTlsOptions BuildTransportTlsOptions(TransportTlsConfiguration? configuration)
    {
        configuration ??= new TransportTlsConfiguration();
        var options = new TransportTlsOptions
        {
            CertificatePath = configuration.CertificatePath,
            CertificateData = configuration.CertificateData,
            CertificateDataSecret = configuration.CertificateDataSecret,
            CertificatePassword = configuration.CertificatePassword,
            CertificatePasswordSecret = configuration.CertificatePasswordSecret,
            AllowUntrustedCertificates = configuration.AllowUntrustedCertificates ?? false,
            CheckCertificateRevocation = configuration.CheckCertificateRevocation ?? true
        };

        foreach (var thumbprint in configuration.AllowedThumbprints)
        {
            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                continue;
            }

            options.AllowedThumbprints.Add(thumbprint.Trim());
        }

        if (!string.IsNullOrWhiteSpace(configuration.ReloadInterval) &&
            TimeSpan.TryParse(configuration.ReloadInterval, out var interval))
        {
            options.ReloadInterval = interval;
        }

        return options;
    }

    private static (LogLevel? Level, List<(string Category, LogLevel Level)> Overrides) ParseLoggingConfiguration(LoggingConfiguration logging)
    {
        LogLevel? minimumLevel = null;
        if (!string.IsNullOrWhiteSpace(logging.Level))
        {
            if (Enum.TryParse<LogLevel>(logging.Level, ignoreCase: true, out var parsed))
            {
                minimumLevel = parsed;
            }
            else
            {
                throw new OmniRelayConfigurationException($"Logging level '{logging.Level}' is not a valid value. Expected values match {nameof(LogLevel)}.");
            }
        }

        var overrides = new List<(string Category, LogLevel Level)>();
        foreach (var entry in logging.Overrides)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            if (!Enum.TryParse<LogLevel>(entry.Value, ignoreCase: true, out var parsed))
            {
                throw new OmniRelayConfigurationException($"Logging override for '{entry.Key}' uses invalid level '{entry.Value}'.");
            }

            overrides.Add((entry.Key, parsed));
        }

        return (minimumLevel, overrides);
    }
}
