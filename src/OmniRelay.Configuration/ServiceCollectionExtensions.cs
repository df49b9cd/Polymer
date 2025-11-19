using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
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
using OmniRelay.Diagnostics;
using OmniRelay.Diagnostics.Alerting;
using OmniRelay.Core.Shards;
using OmniRelay.Core.Shards.ControlPlane;
using OmniRelay.Core.Shards.Hashing;
using OmniRelay.Security.Authorization;
using OmniRelay.Security.Secrets;
using OmniRelay.Transport.Security;
using OmniRelay.Configuration.Internal.TransportPolicy;

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
        TransportPolicyEvaluator.Enforce(snapshot);

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

        services.TryAddSingleton<ShardHashStrategyRegistry>();
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IShardRepository)))
        {
            services.TryAddSingleton(sp =>
            {
                var repository = sp.GetRequiredService<IShardRepository>();
                var strategies = sp.GetRequiredService<ShardHashStrategyRegistry>();
                var timeProvider = sp.GetService<TimeProvider>();
                var logger = sp.GetRequiredService<ILogger<ShardControlPlaneService>>();
                return new ShardControlPlaneService(repository, strategies, timeProvider, logger);
            });
            services.TryAddSingleton(sp =>
            {
                var planeService = sp.GetRequiredService<ShardControlPlaneService>();
                var logger = sp.GetRequiredService<ILogger<ShardControlGrpcService>>();
                return new ShardControlGrpcService(planeService, logger);
            });
        }

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
        telemetryOptions.Otlp.Protocol = otel.Otlp.Protocol ?? "grpc";

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

        services.TryAddSingleton(sp =>
        {
            var documents = LoadPolicyDocuments(configuration);
            var loggerFactory = sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            var timeProvider = sp.GetService<TimeProvider>();
            var defaultLifetime = configuration.Signing.DefaultLifetime ?? TimeSpan.FromHours(1);
            var requireAttestation = configuration.RequireAttestation ?? false;
            return new BootstrapPolicyEvaluator(documents, requireAttestation, defaultLifetime, loggerFactory.CreateLogger<BootstrapPolicyEvaluator>(), timeProvider);
        });

        services.TryAddSingleton<IWorkloadIdentityProvider>(sp =>
        {
            return CreateWorkloadIdentityProvider(sp, configuration, serviceName);
        });
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

    private static IWorkloadIdentityProvider CreateWorkloadIdentityProvider(IServiceProvider services, BootstrapConfiguration configuration, string? serviceName)
    {
        var identity = configuration.Identity ?? new BootstrapIdentityProviderConfiguration();
        var type = (identity.Type ?? "spiffe").Trim().ToLowerInvariant();
        return type switch
        {
            "file" => CreateFileIdentityProvider(identity.File, services),
            _ => CreateSpiffeIdentityProvider(identity.Spiffe, services, serviceName)
        };
    }

    private static SpiffeWorkloadIdentityProvider CreateSpiffeIdentityProvider(SpiffeIdentityProviderConfiguration configuration, IServiceProvider services, string? serviceName)
    {
        if (configuration is null)
        {
            throw new OmniRelayConfigurationException("security.bootstrap.identity.spiffe must be configured.");
        }

        var secretProvider = services.GetService<ISecretProvider>();
        var certificate = LoadSpiffeSigningCertificate(configuration, secretProvider);
        var trustBundle = ResolveTrustBundle(configuration, certificate, secretProvider);
        var trustDomain = string.IsNullOrWhiteSpace(configuration.TrustDomain)
            ? serviceName ?? "omnirelay.mesh"
            : configuration.TrustDomain!;
        var identityTemplate = string.IsNullOrWhiteSpace(configuration.IdentityTemplate)
            ? $"spiffe://{trustDomain}/mesh/{{cluster}}/{{role}}/{{nodeId}}"
            : configuration.IdentityTemplate!;
        var metadata = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(configuration.DefaultMetadata, StringComparer.OrdinalIgnoreCase));
        var options = new SpiffeIdentityProviderOptions
        {
            SigningCertificate = certificate,
            TrustDomain = trustDomain,
            IdentityTemplate = identityTemplate,
            DefaultLifetime = configuration.CertificateLifetime ?? TimeSpan.FromMinutes(30),
            MaximumLifetime = TimeSpan.FromHours(6),
            RenewalWindow = 0.8,
            CertificatePassword = configuration.SigningCertificatePassword,
            TrustBundle = trustBundle,
            DefaultMetadata = metadata
        };
        var timeProvider = services.GetService<TimeProvider>();
        var loggerFactory = services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        return new SpiffeWorkloadIdentityProvider(options, loggerFactory.CreateLogger<SpiffeWorkloadIdentityProvider>(), timeProvider);
    }

    private static FileBootstrapIdentityProvider CreateFileIdentityProvider(FileIdentityProviderConfiguration configuration, IServiceProvider services)
    {
        if (configuration is null)
        {
            throw new OmniRelayConfigurationException("security.bootstrap.identity.file must be configured when the file provider is selected.");
        }

        if (string.IsNullOrWhiteSpace(configuration.CertificatePath) && string.IsNullOrWhiteSpace(configuration.CertificateData))
        {
            throw new OmniRelayConfigurationException("security.bootstrap.identity.file must specify certificatePath or certificateData.");
        }

        byte[] data;
        if (!string.IsNullOrWhiteSpace(configuration.CertificateData))
        {
            data = Convert.FromBase64String(configuration.CertificateData);
        }
        else
        {
            data = File.ReadAllBytes(configuration.CertificatePath!);
        }

        var trustBundle = configuration.AllowExport == true
            ? Convert.ToBase64String(data)
            : null;
        var timeProvider = services.GetService<TimeProvider>();
        return new FileBootstrapIdentityProvider(data, configuration.CertificatePassword, trustBundle, timeProvider);
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

    private static X509Certificate2 LoadSpiffeSigningCertificate(SpiffeIdentityProviderConfiguration configuration, ISecretProvider? secretProvider)
    {
        if (!string.IsNullOrWhiteSpace(configuration.SigningCertificateData))
        {
            var bytes = Convert.FromBase64String(configuration.SigningCertificateData);
            return X509CertificateLoader.LoadPkcs12(bytes, configuration.SigningCertificatePassword, X509KeyStorageFlags.Exportable);
        }

        if (!string.IsNullOrWhiteSpace(configuration.SigningCertificateDataSecret) && secretProvider is not null)
        {
            using var secret = secretProvider.GetSecretSync(configuration.SigningCertificateDataSecret);
            var value = secret?.AsString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                var bytes = Convert.FromBase64String(value);
                return X509CertificateLoader.LoadPkcs12(bytes, configuration.SigningCertificatePassword, X509KeyStorageFlags.Exportable);
            }
        }

        if (!string.IsNullOrWhiteSpace(configuration.SigningCertificatePath))
        {
            if (!File.Exists(configuration.SigningCertificatePath))
            {
                throw new OmniRelayConfigurationException($"SPIFFE signing certificate {configuration.SigningCertificatePath} was not found.");
            }

            var bytes = File.ReadAllBytes(configuration.SigningCertificatePath);
            return X509CertificateLoader.LoadPkcs12(bytes, configuration.SigningCertificatePassword, X509KeyStorageFlags.Exportable);
        }

        throw new OmniRelayConfigurationException("security.bootstrap.identity.spiffe must provide signingCertificatePath, signingCertificateData, or signingCertificateDataSecret.");
    }

    private static string ResolveTrustBundle(SpiffeIdentityProviderConfiguration configuration, X509Certificate2 signingCertificate, ISecretProvider? secretProvider)
    {
        if (!string.IsNullOrWhiteSpace(configuration.TrustBundleData))
        {
            return configuration.TrustBundleData!;
        }

        if (!string.IsNullOrWhiteSpace(configuration.TrustBundleSecret) && secretProvider is not null)
        {
            using var secret = secretProvider.GetSecretSync(configuration.TrustBundleSecret);
            var value = secret?.AsString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value!;
            }
        }

        return Convert.ToBase64String(signingCertificate.Export(X509ContentType.Cert));
    }

    private static List<BootstrapPolicyDocument> LoadPolicyDocuments(BootstrapConfiguration configuration)
    {
        var configs = new List<BootstrapPolicyDocumentConfiguration>();
        configs.AddRange(configuration.Policies.Documents);

        var directory = configuration.Policies.Directory;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var fullPath = Path.IsPathRooted(directory)
                ? directory
                : Path.Combine(AppContext.BaseDirectory, directory!);
            if (Directory.Exists(fullPath))
            {
                foreach (var file in Directory.EnumerateFiles(fullPath, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var document = JsonSerializer.Deserialize(
                            json,
                            ConfigurationJsonContext.Default.BootstrapPolicyDocumentConfiguration);
                        if (document is not null)
                        {
                            configs.Add(document);
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new OmniRelayConfigurationException($"Failed to parse bootstrap policy {file}.", ex);
                    }
                }
            }
        }

        var documents = new List<BootstrapPolicyDocument>();
        foreach (var source in configs)
        {
            var rules = source.Rules.Select(rule => new BootstrapPolicyRule(
                rule.Description ?? string.Empty,
                new ReadOnlyCollection<string>(rule.Roles.ToList()),
                new ReadOnlyCollection<string>(rule.Clusters.ToList()),
                new ReadOnlyCollection<string>(rule.Environments.ToList()),
                new ReadOnlyCollection<string>(rule.AttestationProviders.ToList()),
                new ReadOnlyDictionary<string, string>(rule.Labels),
                new ReadOnlyDictionary<string, string>(rule.Claims),
                rule.Allow,
                rule.IdentityTemplate,
                rule.Lifetime,
                new ReadOnlyDictionary<string, string>(rule.Metadata)))
                .ToArray();

            documents.Add(new BootstrapPolicyDocument(source.Name ?? "default", source.DefaultAllow ?? false, rules));
        }

        return documents;
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
