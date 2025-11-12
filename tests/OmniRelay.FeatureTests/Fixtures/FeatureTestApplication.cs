using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Testing.Platform.Builder;
using OmniRelay.Configuration;
using Xunit;

namespace OmniRelay.FeatureTests.Fixtures;

/// <summary>
/// Spins up a minimal host that wires the OmniRelay dispatcher using the feature test configuration.
/// </summary>
public sealed class FeatureTestApplication : IAsyncLifetime
{
    private IHost? _host;

    public FeatureTestApplication()
        : this(null)
    {
    }

    public FeatureTestApplication(FeatureTestApplicationOptions? options)
    {
        Options = options ?? FeatureTestApplicationOptions.FromEnvironment();
        Containers = new FeatureTestContainers(Options.ContainerOptions);
    }

    public FeatureTestApplicationOptions Options { get; }

    public FeatureTestContainers Containers { get; }

    public IConfigurationRoot Configuration { get; private set; } = default!;

    public IHost FeatureTestHost => _host ?? throw new InvalidOperationException("Feature test host is not initialized.");

    public IServiceProvider Services => FeatureTestHost.Services;

    public async Task InitializeAsync()
    {
        Configuration = BuildConfiguration();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "OmniRelay.FeatureTests",
            EnvironmentName = Options.EnvironmentName,
        });

        builder.Configuration.AddConfiguration(Configuration);

        builder.Services.AddLogging();
        builder.Services.AddOmniRelayDispatcher(Configuration.GetSection("omniRelay"));

        _host = builder.Build();
        await _host.StartAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync().ConfigureAwait(false);
            _host.Dispose();
        }

        await Containers.DisposeAsync().ConfigureAwait(false);
    }

    private IConfigurationRoot BuildConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.featuretests.json", optional: false)
            .AddEnvironmentVariables(prefix: Options.EnvironmentPrefix);

        if (!string.IsNullOrWhiteSpace(Options.AdditionalConfigPath))
        {
            configuration.AddJsonFile(Options.AdditionalConfigPath!, optional: true, reloadOnChange: false);
        }

        return configuration.Build();
    }

    ValueTask IAsyncLifetime.InitializeAsync()
    {
        throw new NotImplementedException();
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        throw new NotImplementedException();
    }

}

public sealed record FeatureTestApplicationOptions
{
    public string EnvironmentName { get; init; } = "FeatureTests";

    public string EnvironmentPrefix { get; init; } = "OMNIRELAY_FEATURETESTS_";

    public string? AdditionalConfigPath { get; init; }
        = Environment.GetEnvironmentVariable("OMNIRELAY_FEATURETESTS_CONFIG");

    public FeatureTestContainerOptions ContainerOptions { get; init; } = FeatureTestContainerOptions.FromEnvironment();

    public static FeatureTestApplicationOptions FromEnvironment()
    {
        var environmentName = Environment.GetEnvironmentVariable("OMNIRELAY_FEATURETESTS_ENVIRONMENT") ?? "FeatureTests";

        return new FeatureTestApplicationOptions
        {
            EnvironmentName = environmentName,
        };
    }
}
