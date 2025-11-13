using System.Net.Http;
using Hugo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniRelay.Configuration;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Grpc;

namespace OmniRelay.Cli;

internal static class CliRuntime
{
    static CliRuntime() => Reset();

    public static IServeHostFactory ServeHostFactory { get; set; } = null!;
    public static IHttpClientFactory HttpClientFactory { get; set; } = null!;
    public static IGrpcInvokerFactory GrpcInvokerFactory { get; set; } = null!;
    public static ICliFileSystem FileSystem { get; set; } = null!;
    public static ICliConsole Console { get; set; } = null!;

    public static void Reset()
    {
        ServeHostFactory = new DefaultServeHostFactory();
        HttpClientFactory = new DefaultHttpClientFactory();
        GrpcInvokerFactory = new DefaultGrpcInvokerFactory();
        FileSystem = new SystemCliFileSystem();
        Console = new SystemCliConsole();
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
    public Task WriteErrorAsync(string message) => System.Console.Error.WriteLineAsync(message);

    public void WriteError(string message) => System.Console.Error.WriteLine(message);

    public void WriteLine(string message) => System.Console.WriteLine(message);
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
        builder.Services.AddOmniRelayDispatcher(builder.Configuration.GetSection(section));
        var host = builder.Build();
        return new DefaultServeHost(host);
    }

    private sealed class DefaultServeHost : IServeHost
    {
        private readonly IHost _host;
        private Dispatcher.Dispatcher? _dispatcher;

        public DefaultServeHost(IHost host) => _host = host;

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
    public HttpClient CreateClient() => new();
}

internal interface IGrpcInvokerFactory
{
    IGrpcInvoker Create(IReadOnlyList<Uri> addresses, string remoteService, GrpcClientRuntimeOptions? runtimeOptions);
}

internal interface IGrpcInvoker : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(Request<ReadOnlyMemory<byte>> request, CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}

internal sealed class DefaultGrpcInvokerFactory : IGrpcInvokerFactory
{
    public IGrpcInvoker Create(IReadOnlyList<Uri> addresses, string remoteService, GrpcClientRuntimeOptions? runtimeOptions)
    {
        var outbound = new GrpcOutbound(addresses, remoteService, clientRuntimeOptions: runtimeOptions);
        return new GrpcInvoker(outbound);
    }

    private sealed class GrpcInvoker : IGrpcInvoker
    {
        private readonly GrpcOutbound _outbound;

        public GrpcInvoker(GrpcOutbound outbound) => _outbound = outbound;

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
