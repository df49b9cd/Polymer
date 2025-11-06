using System;
using System.Linq;
using System.Threading;
using NSubstitute;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public class DispatcherOptionsTests
{
    [Fact]
    public void Constructor_WithBlankServiceName_Throws()
    {
        Assert.Throws<ArgumentException>(() => new DispatcherOptions("  "));
    }

    [Fact]
    public async Task AddLifecycle_WithDuplicateInstance_StartsOnce()
    {
        var options = new DispatcherOptions("test-service");
        var lifecycle = new CountingLifecycle();

        options.AddLifecycle("first", lifecycle);
        options.AddLifecycle("second", lifecycle);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        await dispatcher.StartAsync(CancellationToken.None);
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Equal(1, lifecycle.StartCalls);
        Assert.Equal(1, lifecycle.StopCalls);
    }

    [Fact]
    public void AddTransport_AddsLifecycleComponent()
    {
        var options = new DispatcherOptions("svc");
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("http");
        options.AddTransport(transport);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var components = dispatcher.Introspect().Components;

        Assert.Contains(components, component => component.Name == "http");
    }

    [Fact]
    public void AddUnaryOutbound_RegistersLifecycle()
    {
        var options = new DispatcherOptions("svc");
        options.AddUnaryOutbound("remote", null, Substitute.For<IUnaryOutbound>());

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var outbounds = dispatcher.Introspect().Outbounds.Single();

        Assert.Equal("remote", outbounds.Service);
        Assert.Single(outbounds.Unary);
    }

    private sealed class CountingLifecycle : ILifecycle
    {
        private int _startCalls;
        private int _stopCalls;

        public int StartCalls => _startCalls;
        public int StopCalls => _stopCalls;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCalls);
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopCalls);
            return ValueTask.CompletedTask;
        }
    }
}
