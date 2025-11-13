using Microsoft.Extensions.Configuration;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;

namespace OmniRelay.Cli.UnitTests.Infrastructure;

internal sealed class FakeServeHostFactory : IServeHostFactory
{
    private readonly FakeServeHost _host;

    public FakeServeHostFactory(FakeServeHost? host = null) => _host = host ?? new FakeServeHost();

    public FakeServeHost Host => _host;

    public int CreateCount { get; private set; }

    public IServeHost CreateHost(IConfigurationRoot configuration, string section)
    {
        CreateCount++;
        return _host;
    }
}

internal sealed class FakeServeHost : IServeHost
{
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public Dispatcher.Dispatcher? Dispatcher { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stopped = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeFileSystem : ICliFileSystem
{
    public List<string?> EnsuredDirectories { get; } = new();
    public List<(string Path, string Contents)> Writes { get; } = new();
    public Exception? WriteException { get; set; }

    public void EnsureDirectory(string? path) => EnsuredDirectories.Add(path);

    public void WriteAllText(string path, string contents)
    {
        if (WriteException is not null)
        {
            throw WriteException;
        }

        Writes.Add((path, contents));
    }
}

internal sealed class FakeCliConsole : ICliConsole
{
    public List<string> Lines { get; } = new();
    public List<string> Errors { get; } = new();

    public Task WriteErrorAsync(string message)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }

    public void WriteError(string message) => Errors.Add(message);

    public void WriteLine(string message) => Lines.Add(message);
}

internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient() => new HttpClient(_handler, disposeHandler: false);
}

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

    public List<HttpRequestMessage> Requests { get; } = new();

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
        _responseFactory = responseFactory;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_responseFactory(request));
    }
}
