using System.Diagnostics.CodeAnalysis;
using Hugo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.ControlPlane.Clients;
using OmniRelay.Core;
using OmniRelay.Dispatcher.Config;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;

namespace OmniRelay.Cli;

internal static class CliRuntime
{
    static CliRuntime() => Reset();

    private static ServiceProvider? _controlPlaneServiceProvider;

    public static IServeHostFactory ServeHostFactory { get; set; } = null!;
    public static IHttpClientFactory HttpClientFactory { get; set; } = null!;
    public static IGrpcInvokerFactory GrpcInvokerFactory { get; set; } = null!;
    public static IGrpcControlPlaneClientFactory GrpcControlPlaneClientFactory { get; set; } = null!;
    public static ICliFileSystem FileSystem { get; set; } = null!;
    public static ICliConsole Console { get; set; } = null!;

    public static void Reset()
    {
        _controlPlaneServiceProvider?.Dispose();
        _controlPlaneServiceProvider = BuildControlPlaneServiceProvider();
        var httpControlPlaneFactory = _controlPlaneServiceProvider.GetRequiredService<IHttpControlPlaneClientFactory>();
        var grpcControlPlaneFactory = _controlPlaneServiceProvider.GetRequiredService<IGrpcControlPlaneClientFactory>();

        ServeHostFactory = new DefaultServeHostFactory();
        HttpClientFactory = new DefaultHttpClientFactory(httpControlPlaneFactory);
        GrpcInvokerFactory = new DefaultGrpcInvokerFactory();
        GrpcControlPlaneClientFactory = grpcControlPlaneFactory;
        FileSystem = new SystemCliFileSystem();
        Console = new SystemCliConsole();
    }

    private static ServiceProvider BuildControlPlaneServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddOptions<HttpControlPlaneClientFactoryOptions>()
            .Configure(options =>
            {
                options.Profiles["default"] = new HttpControlPlaneClientProfile
                {
                    BaseAddress = new Uri("http://127.0.0.1"),
                    UseSharedTls = false,
                    Runtime = new HttpClientRuntimeOptions
                    {
                        // Local CLI calls target ad-hoc HTTP endpoints spun up by tests; avoid HTTP/3
                        // negotiation timeouts by sticking to HTTP/1.1 or HTTP/2.
                        EnableHttp3 = false
                    }
                };
            });

        services.AddOptions<GrpcControlPlaneClientFactoryOptions>()
            .Configure(options =>
            {
                options.Profiles["default"] = new GrpcControlPlaneClientProfile
                {
                    Address = new Uri("http://127.0.0.1:17421"),
                    PreferHttp3 = true,
                    UseSharedTls = false,
                    Runtime = new GrpcClientRuntimeOptions
                    {
                        EnableHttp3 = true
                    }
                };
            });

        services.AddSingleton<IHttpControlPlaneClientFactory, HttpControlPlaneClientFactory>();
        services.AddSingleton<IGrpcControlPlaneClientFactory, GrpcControlPlaneClientFactory>();
        return services.BuildServiceProvider();
    }
}

internal interface ICliConsole
{
    Task WriteErrorAsync(string message);
    void WriteError(string message);
    void WriteLine(string message);
}

internal sealed class SystemCliConsole : ICliConsole
{
    public Task WriteErrorAsync(string message) => Console.Error.WriteLineAsync(message);

    public void WriteError(string message) => Console.Error.WriteLine(message);

    public void WriteLine(string message) => Console.WriteLine(message);
}

internal interface ICliFileSystem
{
    void EnsureDirectory(string? path);
    void WriteAllText(string path, string contents);
}

internal sealed class SystemCliFileSystem : ICliFileSystem
{
    public void EnsureDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        Directory.CreateDirectory(path);
    }

    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
}

internal interface IServeHostFactory
{
    [RequiresUnreferencedCode("OmniRelay dispatcher bootstrapping uses reflection and dynamic configuration; it is not trimming/AOT safe.")]
    [RequiresDynamicCode("OmniRelay dispatcher bootstrapping uses reflection and dynamic configuration; it is not trimming/AOT safe.")]
    IServeHost CreateHost(IConfigurationRoot configuration, string section);
}

internal interface IServeHost : IAsyncDisposable
{
    Dispatcher.Dispatcher? Dispatcher { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

internal sealed class DefaultServeHostFactory : IServeHostFactory
{
    [RequiresUnreferencedCode("OmniRelay dispatcher bootstrapping uses reflection and dynamic configuration; it is not trimming/AOT safe.")]
    [RequiresDynamicCode("OmniRelay dispatcher bootstrapping uses reflection and dynamic configuration; it is not trimming/AOT safe.")]
    public IServeHost CreateHost(IConfigurationRoot configuration, string section)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(section))
        {
            throw new ArgumentException("Section name is required.", nameof(section));
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddLogging(static logging => logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        }));
        builder.Services.AddOmniRelayDispatcherFromConfiguration(builder.Configuration.GetSection(section));
        var host = builder.Build();
        return new DefaultServeHost(host);
    }

    private sealed class DefaultServeHost(IHost host) : IServeHost
    {
        private readonly IHost _host = host;
        private Dispatcher.Dispatcher? _dispatcher;

        public Dispatcher.Dispatcher? Dispatcher => _dispatcher ??= _host.Services.GetRequiredService<Dispatcher.Dispatcher>();

        public Task StartAsync(CancellationToken cancellationToken) => _host.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => _host.StopAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            _host.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal interface IHttpClientFactory
{
    HttpClient CreateClient();
}

internal sealed class DefaultHttpClientFactory : IHttpClientFactory
{
    private readonly IHttpControlPlaneClientFactory _factory;

    public DefaultHttpClientFactory(IHttpControlPlaneClientFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public HttpClient CreateClient() => _factory.CreateClient("default").ValueOrThrow();
}

internal interface IGrpcInvokerFactory
{
    OmniRelay.Cli.Core.BenchmarkRunner.IGrpcInvoker Create(IReadOnlyList<Uri> addresses, string remoteService, GrpcClientRuntimeOptions? runtimeOptions);
}

internal sealed class DefaultGrpcInvokerFactory : IGrpcInvokerFactory
{
    public OmniRelay.Cli.Core.BenchmarkRunner.IGrpcInvoker Create(IReadOnlyList<Uri> addresses, string remoteService, GrpcClientRuntimeOptions? runtimeOptions)
    {
        var outbound = new GrpcOutbound(addresses, remoteService, clientRuntimeOptions: runtimeOptions);
        return new GrpcInvoker(outbound);
    }

    private sealed class GrpcInvoker(GrpcOutbound outbound) : OmniRelay.Cli.Core.BenchmarkRunner.IGrpcInvoker
    {
        private readonly GrpcOutbound _outbound = outbound;

        public ValueTask StartAsync(CancellationToken cancellationToken) => _outbound.StartAsync(cancellationToken);

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(Request<ReadOnlyMemory<byte>> request, CancellationToken cancellationToken) =>
            _outbound.CallAsync(request, cancellationToken);

        public ValueTask StopAsync(CancellationToken cancellationToken) => _outbound.StopAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _outbound.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // swallow disposal errors to match previous CLI behavior
            }
        }
    }
}
