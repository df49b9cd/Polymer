using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OmniRelay.ControlPlane.Agent;

internal sealed class MeshAgentHostedService(MeshAgent agent, ILogger<MeshAgentHostedService> logger) : IHostedService
{
    private readonly MeshAgent _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    private readonly ILogger<MeshAgentHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _agent.StartAsync(cancellationToken).ConfigureAwait(false);
        AgentLog.MeshAgentStarted(_logger);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _agent.StopAsync(cancellationToken).ConfigureAwait(false);
        AgentLog.MeshAgentStopped(_logger);
    }
}
