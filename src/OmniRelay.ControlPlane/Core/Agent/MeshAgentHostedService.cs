using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniRelay.Identity;

namespace OmniRelay.ControlPlane.Agent;

internal sealed class MeshAgentHostedService(
    MeshAgent agent,
    ILogger<MeshAgentHostedService> logger,
    AgentCertificateManager? certificates = null) : IHostedService
{
    private readonly MeshAgent _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    private readonly ILogger<MeshAgentHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly AgentCertificateManager? _certificates = certificates;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_certificates is not null)
        {
            await _certificates.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        await _agent.StartAsync(cancellationToken).ConfigureAwait(false);
        AgentLog.MeshAgentStarted(_logger);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _agent.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_certificates is not null)
        {
            await _certificates.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        AgentLog.MeshAgentStopped(_logger);
    }
}
