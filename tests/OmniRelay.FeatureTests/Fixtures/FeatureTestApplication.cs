using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OmniRelay.Core.Gossip;
using OmniRelay.Dispatcher.Config;
using OmniRelay.FeatureTests.Support;
using OmniRelay.Tests;
using OmniRelay.Tests.Support;
using Xunit;

namespace OmniRelay.FeatureTests.Fixtures;

/// <summary>
/// Spins up a minimal host that wires the OmniRelay dispatcher using the feature test configuration.
/// </summary>
public sealed class FeatureTestApplication : IAsyncLifetime
{
    private IHost? _host;

    public FeatureTestApplication()
        : this(FeatureTestApplicationOptions.FromEnvironment())
    {
    }

    public static FeatureTestApplication Create(FeatureTestApplicationOptions options)
        => new(options);

    private FeatureTestApplication(FeatureTestApplicationOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        var reservedPorts = new HashSet<int>();
        ControlPlanePort = Options.ControlPlanePort ?? AllocateUniquePort(reservedPorts);
        if (!reservedPorts.Contains(ControlPlanePort))
        {
            reservedPorts.Add(ControlPlanePort);
        }

        HttpInboundPort = AllocateUniquePort(reservedPorts);
        reservedPorts.Add(HttpInboundPort);

        if (Options.GossipPort.HasValue)
        {
            GossipPort = Options.GossipPort.Value;
        }
        else
        {
            GossipPort = AllocateUniquePort(reservedPorts);
        }
        Containers = new FeatureTestContainers(Options.ContainerOptions);
        Certificate = TestCertificateFactory.EnsureDeveloperCertificateInfo("CN=OmniRelay.FeatureTests");
    }

    public FeatureTestApplicationOptions Options { get; }

    public int ControlPlanePort { get; }

    public int HttpInboundPort { get; }

    public int GossipPort { get; }

    public string ControlPlaneBaseAddress => $"http://127.0.0.1:{ControlPlanePort}";

    public FeatureTestContainers Containers { get; }

    internal TestCertificateInfo Certificate { get; }

    public IConfigurationRoot Configuration { get; private set; } = default!;

    public IHost FeatureTestHost => _host ?? throw new InvalidOperationException("Feature test host is not initialized.");

    public IServiceProvider Services => FeatureTestHost.Services;

    public async ValueTask InitializeAsync()
    {
        Configuration = BuildConfiguration();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "OmniRelay.FeatureTests",
            EnvironmentName = Options.EnvironmentName,
        });

        builder.Configuration.AddConfiguration(Configuration);

        builder.Services.AddLogging();
        builder.Services.Configure<OmniRelayConfigurationOptions>(Configuration.GetSection("omniRelay"));
        builder.Services.AddOmniRelayDispatcherFromConfiguration(Configuration.GetSection("omniRelay"));
        builder.Services.AddSingleton<IMeshGossipAgent, FakeMeshGossipAgent>();
        builder.Services.AddSingleton<IMeshMembershipSnapshotProvider>(sp => sp.GetRequiredService<IMeshGossipAgent>());

        _host = builder.Build();
        await _host.StartAsync().ConfigureAwait(false);

        // Ensure dispatcher lifecycle is started for feature scenarios.
        var dispatcher = _host.Services.GetService<OmniRelay.Dispatcher.Dispatcher>();
        if (dispatcher is not null)
        {
            await dispatcher.StartAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            var dispatcher = _host.Services.GetService<OmniRelay.Dispatcher.Dispatcher>();
            if (dispatcher is not null)
            {
                await dispatcher.StopAsync().ConfigureAwait(false);
            }
            await _host.StopAsync().ConfigureAwait(false);
            _host.Dispose();
        }

        await Containers.DisposeAsync().ConfigureAwait(false);
    }

    private IConfigurationRoot BuildConfiguration()
    {
        var tlsDefaults = TestCertificateConfiguration.BuildTlsDefaults(Certificate, "FeatureTests");
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddInMemoryCollection(tlsDefaults)
            .AddJsonFile("appsettings.featuretests.json", optional: false)
            .AddEnvironmentVariables(prefix: Options.EnvironmentPrefix);

        if (!string.IsNullOrWhiteSpace(Options.AdditionalConfigPath))
        {
            configuration.AddJsonFile(Options.AdditionalConfigPath!, optional: true, reloadOnChange: false);
        }

        var overrides = BuildGossipDefaults(Certificate);
        overrides["omniRelay:inbounds:http:0:urls:0"] = $"http://127.0.0.1:{HttpInboundPort}";
        overrides["omniRelay:diagnostics:controlPlane:httpUrls:0"] = ControlPlaneBaseAddress;
        overrides["omniRelay:mesh:gossip:port"] = GossipPort.ToString(CultureInfo.InvariantCulture);
        overrides["omniRelay:mesh:gossip:advertisePort"] = GossipPort.ToString(CultureInfo.InvariantCulture);
        configuration.AddInMemoryCollection(overrides);

        return configuration.Build();
    }

    private static Dictionary<string, string?> BuildGossipDefaults(TestCertificateInfo certificate) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["omniRelay:mesh:gossip:tls:certificateData"] = certificate.CertificateData,
            ["omniRelay:mesh:gossip:tls:certificatePassword"] = certificate.Password
        };

    private static int AllocateUniquePort(ISet<int> reserved)
    {
        while (true)
        {
            var port = TestPortAllocator.GetRandomPort();
            if (reserved.Add(port))
            {
                return port;
            }
        }
    }
}

public sealed record FeatureTestApplicationOptions
{
    public string EnvironmentName { get; init; } = "FeatureTests";

    public string EnvironmentPrefix { get; init; } = "OMNIRELAY_FEATURETESTS_";

    public string? AdditionalConfigPath { get; init; }
        = Environment.GetEnvironmentVariable("OMNIRELAY_FEATURETESTS_CONFIG");

    public FeatureTestContainerOptions ContainerOptions { get; init; } = FeatureTestContainerOptions.FromEnvironment();

    public int? ControlPlanePort { get; init; }

    public int? GossipPort { get; init; }

    public static FeatureTestApplicationOptions FromEnvironment()
    {
        var environmentName = Environment.GetEnvironmentVariable("OMNIRELAY_FEATURETESTS_ENVIRONMENT") ?? "FeatureTests";

        return new FeatureTestApplicationOptions
        {
            EnvironmentName = environmentName,
        };
    }
}
