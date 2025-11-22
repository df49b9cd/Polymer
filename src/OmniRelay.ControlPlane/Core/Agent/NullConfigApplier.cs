using OmniRelay.ControlPlane.ControlProtocol;
using Microsoft.Extensions.Logging;

namespace OmniRelay.ControlPlane.Agent;

/// <summary>Stub applier that just logs; replace with real config application.</summary>
public sealed class NullConfigApplier : IControlPlaneConfigApplier
{
    private readonly ILogger<NullConfigApplier> _logger;

    public NullConfigApplier(ILogger<NullConfigApplier> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task ApplyAsync(string version, byte[] payload, CancellationToken cancellationToken = default)
    {
        AgentLog.ConfigAppliedStub(_logger, version, payload.Length);
        return Task.CompletedTask;
    }
}
